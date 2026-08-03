using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Evals;

public record EvalScenarioItemVm(
    Guid Id,
    string Name,
    string PromptKey,
    string UserInput,
    string Criteria,
    bool IsActive,
    DateTime CreatedAt,
    // Prompt = một lượt; Interview = phỏng vấn mô phỏng nhiều lượt (UserInput là hồ sơ persona).
    EvalScenarioKind Kind,
    // Điểm của lần chấm GẦN NHẤT (mọi run) — để nhìn golden set là thấy ngay chỗ yếu, không phải mở
    // từng run ra dò. null = chưa từng chạy, hoặc lần gần nhất lỗi nên không có điểm.
    int? LastScore,
    DateTime? LastRunAt);

public record EvalRunItemVm(
    Guid Id,
    string? Note,
    string? PromptKey,
    string TargetModelName,
    string JudgeModelName,
    EvalRunStatus Status,
    int ScenarioCount,
    int CompletedCount,
    double? AverageScore,
    long TotalTokens,
    decimal TotalCost,
    string? Error,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? CreatedByUsername,
    // Số scenario chạy hỏng trong run: "4.10 điểm" đọc rất khác khi biết trong đó có 3 scenario chết.
    int FailedCount)
{
    public TimeSpan? Duration => StartedAt is { } started && FinishedAt is { } finished ? finished - started : null;
}

public record EvalModelOptionVm(Guid Id, string ModelId);

/// <summary>
/// Ước lượng chi phí cho MỘT lựa chọn bộ lọc prompt của modal "Chạy Eval" (PromptKey null = tất cả
/// scenario đang bật). Dựa trên chi phí THỰC của các run gần đây chứ không phải công thức token lý
/// thuyết — nút chạy vốn chỉ ghi "tốn token thật" mà không nói bao nhiêu.
/// </summary>
public record EvalCostEstimateVm(string? PromptKey, int ScenarioCount, decimal EstimatedCost, bool HasHistory);

public record EvalPageVm(
    IReadOnlyList<EvalScenarioItemVm> Scenarios,
    IReadOnlyList<EvalRunItemVm> Runs,
    IReadOnlyList<string> PromptKeys,
    IReadOnlyList<EvalModelOptionVm> Models,
    IReadOnlyList<EvalCostEstimateVm> CostEstimates,
    // Bộ lọc + phân trang PHÍA SERVER của bảng Runs (bảng Scenarios vẫn phân trang client như cũ).
    IReadOnlyList<string> RunPromptKeys,
    string? RunPromptKeyFilter,
    EvalRunStatus? RunStatusFilter,
    int Page,
    int PageSize,
    int TotalRunCount)
{
    public int TotalPages => TotalRunCount == 0 ? 1 : (int)Math.Ceiling(TotalRunCount / (double)PageSize);

    public bool HasRunFilter => RunPromptKeyFilter != null || RunStatusFilter != null;
}

/// <summary>Dữ liệu trang Prompt Evals: golden set + các run (lọc/phân trang) + danh mục prompt/model cho form.</summary>
public class GetEvalPageQuery
{
    public const int DefaultPageSize = 10;

    /// <summary>
    /// Số run gần nhất lấy làm mẫu ước lượng chi phí. Đủ nhiều để trung bình có nghĩa, đủ ít để không kéo
    /// cả lịch sử EvalResults lên bộ nhớ mỗi lần mở trang.
    /// </summary>
    private const int CostHistoryRuns = 5;

    private readonly AppDbContext _db;
    private readonly PromptFileCatalog _promptCatalog;

    public GetEvalPageQuery(AppDbContext db, PromptFileCatalog promptCatalog)
    {
        _db = db;
        _promptCatalog = promptCatalog;
    }

    public async Task<EvalPageVm> ExecuteAsync(
        string? runPromptKey = null,
        EvalRunStatus? runStatus = null,
        int page = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = DefaultPageSize;
        runPromptKey = string.IsNullOrWhiteSpace(runPromptKey) ? null : runPromptKey.Trim();

        var scenarios = await LoadScenariosAsync(cancellationToken);
        var runs = await LoadRunsAsync(runPromptKey, runStatus, page, pageSize, cancellationToken);

        var models = await _db.AiModels
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.ModelId)
            .Select(x => new EvalModelOptionVm(x.Id, x.ModelId))
            .ToListAsync(cancellationToken);

        var runPromptKeys = await _db.EvalRuns
            .AsNoTracking()
            .Where(x => x.PromptKey != null)
            .Select(x => x.PromptKey!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        return new EvalPageVm(
            scenarios,
            runs.Items,
            _promptCatalog.PromptKeys,
            models,
            await BuildCostEstimatesAsync(scenarios, cancellationToken),
            runPromptKeys,
            runPromptKey,
            runStatus,
            page,
            pageSize,
            runs.TotalCount);
    }

    private async Task<List<EvalScenarioItemVm>> LoadScenariosAsync(CancellationToken cancellationToken)
    {
        // Kết quả gần nhất của từng scenario lấy bằng truy vấn tương quan (OUTER APPLY trên SQL Server):
        // golden set chỉ vài chục dòng nên rẻ, và tránh phải kéo cả bảng EvalResults về gộp trong bộ nhớ.
        var rows = await _db.EvalScenarios
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                Scenario = new EvalScenarioItemVm(
                    x.Id, x.Name, x.PromptKey, x.UserInput, x.Criteria, x.IsActive, x.CreatedAt, x.Kind, null, null),
                LastScore = _db.EvalResults
                    .Where(r => r.EvalScenarioId == x.Id)
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => r.Score)
                    .FirstOrDefault(),
                LastRunAt = _db.EvalResults
                    .Where(r => r.EvalScenarioId == x.Id)
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => (DateTime?)r.CreatedAt)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => r.Scenario with { LastScore = r.LastScore, LastRunAt = r.LastRunAt })
            .ToList();
    }

    private async Task<(List<EvalRunItemVm> Items, int TotalCount)> LoadRunsAsync(
        string? promptKey, EvalRunStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = _db.EvalRuns.AsNoTracking();
        if (promptKey != null)
            query = query.Where(x => x.PromptKey == promptKey);
        if (status is { } s)
            query = query.Where(x => x.Status == s);

        var totalCount = await query.CountAsync(cancellationToken);

        var runs = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new EvalRunItemVm(
                x.Id, x.Note, x.PromptKey, x.TargetModelName, x.JudgeModelName, x.Status,
                x.ScenarioCount, x.CompletedCount, x.AverageScore, x.TotalTokens, x.TotalCost, x.Error,
                x.CreatedAt, x.StartedAt, x.FinishedAt, x.CreatedByUsername, 0))
            .ToListAsync(cancellationToken);

        if (runs.Count == 0)
            return (runs, totalCount);

        var runIds = runs.Select(x => x.Id).ToList();
        var failedCounts = await _db.EvalResults
            .AsNoTracking()
            .Where(x => runIds.Contains(x.EvalRunId) && !x.IsSuccess)
            .GroupBy(x => x.EvalRunId)
            .Select(g => new { RunId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RunId, x => x.Count, cancellationToken);

        return (
            runs.Select(x => failedCounts.TryGetValue(x.Id, out var failed) ? x with { FailedCount = failed } : x).ToList(),
            totalCount);
    }

    /// <summary>
    /// Ước lượng chi phí một run cho từng lựa chọn bộ lọc prompt, dựa trên chi phí thật của các run gần
    /// đây: mỗi scenario lấy chi phí trung bình CỦA CHÍNH NÓ (sát nhất — scenario phỏng vấn đắt gấp
    /// nhiều lần scenario một lượt), thiếu thì mượn trung bình của các scenario cùng kiểu.
    /// <para>
    /// Cộng/chia làm trong bộ nhớ chứ không đẩy xuống SQL: Sqlite (dùng ở Development và test) lưu decimal
    /// dạng TEXT nên phép cộng/AVG phía DB cho ra số sai.
    /// </para>
    /// </summary>
    private async Task<List<EvalCostEstimateVm>> BuildCostEstimatesAsync(
        List<EvalScenarioItemVm> scenarios, CancellationToken cancellationToken)
    {
        var activeScenarios = scenarios.Where(x => x.IsActive).ToList();
        if (activeScenarios.Count == 0)
            return new List<EvalCostEstimateVm>();

        var recentRunIds = await _db.EvalRuns
            .AsNoTracking()
            .Where(x => x.Status == EvalRunStatus.Completed)
            .OrderByDescending(x => x.CreatedAt)
            .Take(CostHistoryRuns)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var samples = recentRunIds.Count == 0
            ? new List<CostSample>()
            : await _db.EvalResults
                .AsNoTracking()
                .Where(x => recentRunIds.Contains(x.EvalRunId) && x.IsSuccess)
                .Select(x => new CostSample(x.EvalScenarioId, x.TargetCost, x.JudgeCost))
                .ToListAsync(cancellationToken);

        var costByScenario = samples
            .GroupBy(x => x.ScenarioId)
            .ToDictionary(g => g.Key, g => g.Average(x => x.TargetCost + x.JudgeCost));

        var kindByScenario = scenarios.ToDictionary(x => x.Id, x => x.Kind);
        var costByKind = samples
            .Where(x => kindByScenario.ContainsKey(x.ScenarioId))
            .GroupBy(x => kindByScenario[x.ScenarioId])
            .ToDictionary(g => g.Key, g => g.Average(x => x.TargetCost + x.JudgeCost));

        decimal EstimateFor(EvalScenarioItemVm scenario) =>
            costByScenario.TryGetValue(scenario.Id, out var own) ? own
            : costByKind.TryGetValue(scenario.Kind, out var kind) ? kind
            : 0m;

        EvalCostEstimateVm Estimate(string? promptKey, IReadOnlyCollection<EvalScenarioItemVm> group)
        {
            var total = group.Sum(EstimateFor);
            return new EvalCostEstimateVm(promptKey, group.Count, total, HasHistory: total > 0);
        }

        var estimates = new List<EvalCostEstimateVm> { Estimate(null, activeScenarios) };
        estimates.AddRange(activeScenarios
            .GroupBy(x => x.PromptKey)
            .OrderBy(g => g.Key)
            .Select(g => Estimate(g.Key, g.ToList())));

        return estimates;
    }

    private record CostSample(Guid ScenarioId, decimal TargetCost, decimal JudgeCost);
}
