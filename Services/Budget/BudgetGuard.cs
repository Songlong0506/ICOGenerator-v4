using ICOGenerator.Data;
using ICOGenerator.Services.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ICOGenerator.Services.Budget;

/// <summary>
/// Hiện thực circuit-breaker ngân sách: cộng chi phí đã ghi trong kỳ rồi so với trần USD trước khi cho gọi
/// model. Chi phí tính y hệt trang Usage (token × đơn giá theo ModelId, qua <see cref="LlmCost"/>), nên trần
/// admin đặt khớp đúng con số họ thấy. Đếm CẢ lời gọi thành công lẫn thất bại — giống Usage và an toàn về phía
/// "thà chặn sớm" cho một chốt chặn tiền.
/// <para>
/// Guard chạy TRƯỚC MỖI lời gọi model (một agent run 40 bước = 40 lần kiểm) nên bản tổng chi phí được cache
/// <see cref="SpendCacheDuration"/> trong IMemoryCache (chia sẻ cả tiến trình): bảng log chỉ bị quét lại tối đa
/// một lần mỗi cửa sổ cache thay vì mỗi lời gọi. Đổi lại trần có thể bị vượt thêm lượng chi tiêu của đúng
/// khoảng cache đó — chấp nhận được với một chốt chặn ngân sách (vốn đã đo bằng kỳ Daily/Monthly).
/// </para>
/// </summary>
public sealed class BudgetGuard : IBudgetGuard
{
    // Ngắn đủ để trần không bị "trượt" đáng kể, dài đủ để một agent run nhiều bước chỉ tốn ~vài lượt quét.
    private static readonly TimeSpan SpendCacheDuration = TimeSpan.FromSeconds(15);

    private readonly AppDbContext _db;
    private readonly BudgetPolicy _policy;
    private readonly IMemoryCache _cache;

    public BudgetGuard(AppDbContext db, BudgetPolicy policy, IMemoryCache cache)
    {
        _db = db;
        _policy = policy;
        _cache = cache;
    }

    // Ảnh chụp chi tiêu trong kỳ, đã quy ra USD: tổng hệ thống + theo từng project.
    private sealed record SpendSnapshot(decimal SystemUsd, IReadOnlyDictionary<Guid, decimal> ProjectUsd);

    public async Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        // Chưa cấu hình trần nào ⇒ không bao giờ chạm DB trên hot-path (mỗi lời gọi model đều qua đây).
        if (!_policy.HasAnyLimit)
            return;

        var since = _policy.WindowStart(DateTime.UtcNow);

        // Key theo mốc đầu kỳ: sang kỳ mới (ngày/tháng) là key mới nên không dùng nhầm số liệu kỳ cũ;
        // trong kỳ, TTL 15s là van làm mới duy nhất.
        var snapshot = await _cache.GetOrCreateAsync($"BudgetGuard.Spend:{since:O}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = SpendCacheDuration;
            return LoadSpendSnapshotAsync(since, cancellationToken);
        });

        if (snapshot == null)
            return; // phòng thủ — GetOrCreateAsync không trả null vì factory luôn trả snapshot

        // So sánh '>=': đã chạm trần là chặn (lời gọi kế chỉ làm vượt thêm).
        if (_policy.SystemUsdLimit > 0 && snapshot.SystemUsd >= _policy.SystemUsdLimit)
            throw new BudgetExceededException(BudgetScope.System, snapshot.SystemUsd, _policy.SystemUsdLimit, _policy.Period);

        if (_policy.PerProjectUsdLimit > 0
            && snapshot.ProjectUsd.TryGetValue(projectId, out var projectUsd)
            && projectUsd >= _policy.PerProjectUsdLimit)
        {
            throw new BudgetExceededException(BudgetScope.Project, projectUsd, _policy.PerProjectUsdLimit, _policy.Period);
        }
    }

    private async Task<SpendSnapshot> LoadSpendSnapshotAsync(DateTime since, CancellationToken cancellationToken)
    {
        // Đơn giá theo ModelId. Log chỉ lưu ModelId dạng chuỗi (không FK) nên tra giá bằng ModelId — phản chiếu
        // GetUsageOverviewQuery. Cùng ModelId ở nhiều endpoint ⇒ gộp, lấy bản đầu.
        var priceByModelId = (await _db.AiModels
                .AsNoTracking()
                .Select(m => new { m.ModelId, m.InputPricePerMillionTokens, m.OutputPricePerMillionTokens })
                .ToListAsync(cancellationToken))
            .GroupBy(m => m.ModelId)
            .ToDictionary(
                g => g.Key ?? string.Empty,
                g => (Input: g.First().InputPricePerMillionTokens, Output: g.First().OutputPricePerMillionTokens),
                StringComparer.OrdinalIgnoreCase);

        // Một lượt quét cửa sổ (đi qua index CreatedAt của AgentModelCallLogs), gom theo (project, model)
        // ở DB — số dòng bị chặn bởi (#project × #model), nhỏ. Rồi quy ra cả tổng hệ thống lẫn riêng từng
        // project trong bộ nhớ (cost tuyến tính theo token nên tổng theo dòng = tổng theo nhóm thô).
        var rows = await _db.AgentModelCallLogs
            .AsNoTracking()
            .Where(x => x.CreatedAt >= since)
            .GroupBy(x => new { x.ProjectId, x.ModelId })
            .Select(g => new
            {
                g.Key.ProjectId,
                g.Key.ModelId,
                Prompt = g.Sum(x => (long)x.PromptTokens),
                Completion = g.Sum(x => (long)x.CompletionTokens)
            })
            .ToListAsync(cancellationToken);

        decimal CostOf(string? modelId, long prompt, long completion)
            => modelId != null && priceByModelId.TryGetValue(modelId, out var p)
                ? LlmCost.Usd(prompt, completion, p.Input, p.Output)
                : 0m;

        var projectUsd = rows
            .GroupBy(r => r.ProjectId)
            .ToDictionary(g => g.Key, g => g.Sum(r => CostOf(r.ModelId, r.Prompt, r.Completion)));

        return new SpendSnapshot(projectUsd.Values.Sum(), projectUsd);
    }
}
