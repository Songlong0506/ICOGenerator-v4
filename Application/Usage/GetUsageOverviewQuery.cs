using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Usage;

public record MonthlyUsageItem(int Year, int Month, long PromptTokens, long CompletionTokens, long TotalTokens, int CallCount, decimal Cost);

public record ProjectUsageItem(Guid ProjectId, string ProjectName, long PromptTokens, long CompletionTokens, long TotalTokens, int CallCount, DateTime? LastCallAt, decimal Cost);

public record ModelUsageItem(string ModelId, string ModelName, long PromptTokens, long CompletionTokens, long TotalTokens, int CallCount, decimal InputPricePerMillionTokens, decimal OutputPricePerMillionTokens, bool HasPrice, decimal Cost);

public record RunUsageItem(Guid RunId, string RunName, string ProjectName, long PromptTokens, long CompletionTokens, long TotalTokens, int CallCount, DateTime? LastCallAt, decimal Cost);

public record UsageOverviewVm(
    long TotalTokens,
    long TotalPromptTokens,
    long TotalCompletionTokens,
    int TotalCalls,
    long CurrentMonthTokens,
    decimal TotalCost,
    decimal CurrentMonthCost,
    bool HasAnyPricing,
    IReadOnlyList<MonthlyUsageItem> MonthlyUsage,
    IReadOnlyList<ModelUsageItem> ModelUsage,
    IReadOnlyList<ProjectUsageItem> ProjectUsage,
    IReadOnlyList<RunUsageItem> RunUsage);

public class GetUsageOverviewQuery
{
    private const int MonthsToShow = 12;
    private const int MaxRunsToShow = 20;

    private readonly AppDbContext _db;
    public GetUsageOverviewQuery(AppDbContext db) => _db = db;

    public async Task<UsageOverviewVm> ExecuteAsync()
    {
        // Bảng giá theo ModelId. Log chỉ lưu ModelId/ModelName dạng chuỗi (không FK), nên ta tra giá bằng
        // ModelId. Cùng một ModelId có thể có nhiều bản ghi AiModel (khác endpoint) → gộp lại, lấy bản đầu.
        var priceByModelId = (await _db.AiModels
                .AsNoTracking()
                .Select(m => new { m.ModelId, m.InputPricePerMillionTokens, m.OutputPricePerMillionTokens })
                .ToListAsync())
            .GroupBy(m => m.ModelId)
            .ToDictionary(
                g => g.Key ?? string.Empty,
                g => (Input: g.First().InputPricePerMillionTokens, Output: g.First().OutputPricePerMillionTokens),
                StringComparer.OrdinalIgnoreCase);

        // Quy token ra USD theo đơn giá của model. Model không có giá (đã xóa / tự host để 0) → chi phí 0.
        decimal CostFor(string? modelId, long prompt, long completion)
            => modelId != null && priceByModelId.TryGetValue(modelId, out var p)
                ? prompt / 1_000_000m * p.Input + completion / 1_000_000m * p.Output
                : 0m;

        bool HasPrice(string? modelId)
            => modelId != null && priceByModelId.TryGetValue(modelId, out var p) && (p.Input > 0 || p.Output > 0);

        var now = DateTime.UtcNow;
        var firstMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(MonthsToShow - 1));

        // ----- Theo model (toàn thời gian); cũng là nguồn cộng ra tổng token + tổng chi phí -----
        // Gom theo cả ModelId + ModelName (giống cách code cũ gom theo cột chuỗi) thay vì MAX(ModelName) trên
        // cột nvarchar(max). ModelId ↔ ModelName gần như 1:1 nên bảng không bị tách dòng; tổng vẫn đúng.
        var modelRaw = await _db.AgentModelCallLogs
            .AsNoTracking()
            .GroupBy(x => new { x.ModelId, x.ModelName })
            .Select(g => new
            {
                g.Key.ModelId,
                g.Key.ModelName,
                PromptTokens = g.Sum(x => (long)x.PromptTokens),
                CompletionTokens = g.Sum(x => (long)x.CompletionTokens),
                TotalTokens = g.Sum(x => (long)x.TotalTokens),
                CallCount = g.Count()
            })
            .ToListAsync();

        var models = modelRaw
            .Select(x =>
            {
                var price = priceByModelId.TryGetValue(x.ModelId ?? string.Empty, out var p) ? p : (Input: 0m, Output: 0m);
                return new ModelUsageItem(
                    x.ModelId ?? string.Empty,
                    string.IsNullOrWhiteSpace(x.ModelName) ? (x.ModelId ?? "(unknown)") : x.ModelName,
                    x.PromptTokens,
                    x.CompletionTokens,
                    x.TotalTokens,
                    x.CallCount,
                    price.Input,
                    price.Output,
                    HasPrice(x.ModelId),
                    CostFor(x.ModelId, x.PromptTokens, x.CompletionTokens));
            })
            .OrderByDescending(x => x.Cost)
            .ThenByDescending(x => x.TotalTokens)
            .ToList();

        var totalTokens = models.Sum(x => x.TotalTokens);
        var totalPrompt = models.Sum(x => x.PromptTokens);
        var totalCompletion = models.Sum(x => x.CompletionTokens);
        var totalCalls = models.Sum(x => x.CallCount);
        var totalCost = models.Sum(x => x.Cost);

        // ----- Theo tháng (12 tháng gần nhất), tách theo ModelId để áp đơn giá rồi gộp lại theo tháng -----
        var monthlyRaw = await _db.AgentModelCallLogs
            .AsNoTracking()
            .Where(x => x.CreatedAt >= firstMonth)
            .GroupBy(x => new { x.CreatedAt.Year, x.CreatedAt.Month, x.ModelId })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                g.Key.ModelId,
                PromptTokens = g.Sum(x => (long)x.PromptTokens),
                CompletionTokens = g.Sum(x => (long)x.CompletionTokens),
                TotalTokens = g.Sum(x => (long)x.TotalTokens),
                CallCount = g.Count()
            })
            .ToListAsync();

        var monthly = Enumerable.Range(0, MonthsToShow)
            .Select(offset =>
            {
                var month = firstMonth.AddMonths(offset);
                var rows = monthlyRaw.Where(x => x.Year == month.Year && x.Month == month.Month).ToList();
                return new MonthlyUsageItem(
                    month.Year,
                    month.Month,
                    rows.Sum(x => x.PromptTokens),
                    rows.Sum(x => x.CompletionTokens),
                    rows.Sum(x => x.TotalTokens),
                    rows.Sum(x => x.CallCount),
                    rows.Sum(x => CostFor(x.ModelId, x.PromptTokens, x.CompletionTokens)));
            })
            .ToList();

        // ----- Theo project, tách theo ModelId để tính chi phí rồi gộp lại theo project -----
        var projectModelRaw = await _db.AgentModelCallLogs
            .AsNoTracking()
            .GroupBy(x => new { x.ProjectId, ProjectName = x.Project!.Name, x.ModelId })
            .Select(g => new
            {
                g.Key.ProjectId,
                g.Key.ProjectName,
                g.Key.ModelId,
                PromptTokens = g.Sum(x => (long)x.PromptTokens),
                CompletionTokens = g.Sum(x => (long)x.CompletionTokens),
                TotalTokens = g.Sum(x => (long)x.TotalTokens),
                CallCount = g.Count(),
                LastCalledAt = g.Max(x => (DateTime?)x.CreatedAt)
            })
            .ToListAsync();

        var projects = projectModelRaw
            .GroupBy(x => new { x.ProjectId, x.ProjectName })
            .Select(g => new ProjectUsageItem(
                g.Key.ProjectId,
                g.Key.ProjectName,
                g.Sum(x => x.PromptTokens),
                g.Sum(x => x.CompletionTokens),
                g.Sum(x => x.TotalTokens),
                g.Sum(x => x.CallCount),
                g.Max(x => x.LastCalledAt),
                g.Sum(x => CostFor(x.ModelId, x.PromptTokens, x.CompletionTokens))))
            .OrderByDescending(x => x.TotalTokens)
            .ToList();

        // ----- Theo run: chỉ các log có WorkflowRunId (agent task của workflow); tách theo ModelId để tính giá -----
        var runModelRaw = await _db.AgentModelCallLogs
            .AsNoTracking()
            .Where(x => x.WorkflowRunId != null)
            .GroupBy(x => new { x.WorkflowRunId, x.ModelId })
            .Select(g => new
            {
                g.Key.WorkflowRunId,
                g.Key.ModelId,
                PromptTokens = g.Sum(x => (long)x.PromptTokens),
                CompletionTokens = g.Sum(x => (long)x.CompletionTokens),
                TotalTokens = g.Sum(x => (long)x.TotalTokens),
                CallCount = g.Count(),
                LastCalledAt = g.Max(x => (DateTime?)x.CreatedAt)
            })
            .ToListAsync();

        // Lấy tên run + project cho các run xuất hiện trong log (WorkflowRunId không có FK nên join thủ công).
        var runIds = runModelRaw.Select(x => x.WorkflowRunId!.Value).Distinct().ToList();
        var runMetaById = (await _db.WorkflowRuns
                .AsNoTracking()
                .Where(r => runIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Name, ProjectName = r.Project.Name })
                .ToListAsync())
            .ToDictionary(r => r.Id);

        var runs = runModelRaw
            .GroupBy(x => x.WorkflowRunId!.Value)
            .Select(g =>
            {
                runMetaById.TryGetValue(g.Key, out var meta);
                return new RunUsageItem(
                    g.Key,
                    meta?.Name ?? "(run đã xóa)",
                    meta?.ProjectName ?? "–",
                    g.Sum(x => x.PromptTokens),
                    g.Sum(x => x.CompletionTokens),
                    g.Sum(x => x.TotalTokens),
                    g.Sum(x => x.CallCount),
                    g.Max(x => x.LastCalledAt),
                    g.Sum(x => CostFor(x.ModelId, x.PromptTokens, x.CompletionTokens)));
            })
            .OrderByDescending(x => x.LastCallAt)
            .Take(MaxRunsToShow)
            .ToList();

        var hasAnyPricing = priceByModelId.Values.Any(p => p.Input > 0 || p.Output > 0);

        return new UsageOverviewVm(
            totalTokens,
            totalPrompt,
            totalCompletion,
            totalCalls,
            monthly[^1].TotalTokens,
            totalCost,
            monthly[^1].Cost,
            hasAnyPricing,
            monthly,
            models,
            projects,
            runs);
    }
}
