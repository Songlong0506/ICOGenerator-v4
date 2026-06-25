using ICOGenerator.Data;
using ICOGenerator.Services.Llm;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Budget;

/// <summary>
/// Hiện thực circuit-breaker ngân sách: cộng chi phí đã ghi trong kỳ rồi so với trần USD trước khi cho gọi
/// model. Chi phí tính y hệt trang Usage (token × đơn giá theo ModelId, qua <see cref="LlmCost"/>), nên trần
/// admin đặt khớp đúng con số họ thấy. Đếm CẢ lời gọi thành công lẫn thất bại — giống Usage và an toàn về phía
/// "thà chặn sớm" cho một chốt chặn tiền.
/// </summary>
public sealed class BudgetGuard : IBudgetGuard
{
    private readonly AppDbContext _db;
    private readonly BudgetPolicy _policy;

    public BudgetGuard(AppDbContext db, BudgetPolicy policy)
    {
        _db = db;
        _policy = policy;
    }

    public async Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        // Chưa cấu hình trần nào ⇒ không bao giờ chạm DB trên hot-path (mỗi lời gọi model đều qua đây).
        if (!_policy.HasAnyLimit)
            return;

        var since = _policy.WindowStart(DateTime.UtcNow);

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

        // Một lượt quét cửa sổ, gom theo (project, model) ở DB — số dòng bị chặn bởi (#project × #model), nhỏ.
        // Rồi quy ra cả tổng hệ thống lẫn riêng project trong bộ nhớ (cost tuyến tính theo token nên tổng theo
        // dòng = tổng theo nhóm thô).
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

        // So sánh '>=': đã chạm trần là chặn (lời gọi kế chỉ làm vượt thêm).
        if (_policy.SystemUsdLimit > 0)
        {
            var systemUsd = rows.Sum(r => CostOf(r.ModelId, r.Prompt, r.Completion));
            if (systemUsd >= _policy.SystemUsdLimit)
                throw new BudgetExceededException(BudgetScope.System, systemUsd, _policy.SystemUsdLimit, _policy.Period);
        }

        if (_policy.PerProjectUsdLimit > 0)
        {
            var projectUsd = rows.Where(r => r.ProjectId == projectId).Sum(r => CostOf(r.ModelId, r.Prompt, r.Completion));
            if (projectUsd >= _policy.PerProjectUsdLimit)
                throw new BudgetExceededException(BudgetScope.Project, projectUsd, _policy.PerProjectUsdLimit, _policy.Period);
        }
    }
}
