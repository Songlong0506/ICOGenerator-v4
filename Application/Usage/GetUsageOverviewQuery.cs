using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Usage;

public record MonthlyUsageItem(int Year, int Month, long PromptTokens, long CompletionTokens, long TotalTokens, int CallCount, decimal Cost);

public record ProjectUsageItem(Guid ProjectId, string ProjectName, long PromptTokens, long CompletionTokens, long TotalTokens, int CallCount, DateTime? LastCallAt, decimal Cost);

public record ModelUsageItem(string ModelId, string ModelName, long PromptTokens, long CompletionTokens, long TotalTokens, int CallCount, decimal InputPricePerMillionTokens, decimal OutputPricePerMillionTokens, bool HasPrice, decimal Cost);

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
    IReadOnlyList<ProjectUsageItem> ProjectUsage);

public class GetUsageOverviewQuery
{
    private const int MonthsToShow = 12;

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

        // One pass over the whole call-log table, aggregated at the finest grain every section below
        // needs — project + run + month + model. Each section re-aggregates this in memory: sums, counts
        // and maxes all compose, and per-token cost is linear in (prompt, completion) for a fixed model,
        // so summing CostFor over these rows equals computing it on the coarser groups. One table scan
        // instead of four. ModelId/ModelName/ProjectName are bounded columns (≤ nvarchar(200)).
        var logRaw = await _db.AgentModelCallLogs
            .AsNoTracking()
            .GroupBy(x => new
            {
                x.ProjectId,
                ProjectName = x.Project!.Name,
                x.WorkflowRunId,
                x.CreatedAt.Year,
                x.CreatedAt.Month,
                x.ModelId,
                x.ModelName
            })
            .Select(g => new
            {
                g.Key.ProjectId,
                g.Key.ProjectName,
                g.Key.WorkflowRunId,
                g.Key.Year,
                g.Key.Month,
                g.Key.ModelId,
                g.Key.ModelName,
                PromptTokens = g.Sum(x => (long)x.PromptTokens),
                CompletionTokens = g.Sum(x => (long)x.CompletionTokens),
                TotalTokens = g.Sum(x => (long)x.TotalTokens),
                CallCount = g.Count(),
                LastCalledAt = g.Max(x => (DateTime?)x.CreatedAt)
            })
            .ToListAsync();

        // ----- Theo model (toàn thời gian); cũng là nguồn cộng ra tổng token + tổng chi phí -----
        // Gom theo cả ModelId + ModelName (ModelId ↔ ModelName gần như 1:1 nên bảng không bị tách dòng).
        var models = logRaw
            .GroupBy(x => new { x.ModelId, x.ModelName })
            .Select(g =>
            {
                var modelId = g.Key.ModelId;
                var promptTokens = g.Sum(x => x.PromptTokens);
                var completionTokens = g.Sum(x => x.CompletionTokens);
                var totalTokens = g.Sum(x => x.TotalTokens);
                var callCount = g.Sum(x => x.CallCount);
                var price = priceByModelId.TryGetValue(modelId ?? string.Empty, out var p) ? p : (Input: 0m, Output: 0m);
                return new ModelUsageItem(
                    modelId ?? string.Empty,
                    string.IsNullOrWhiteSpace(g.Key.ModelName) ? (modelId ?? "(unknown)") : g.Key.ModelName,
                    promptTokens,
                    completionTokens,
                    totalTokens,
                    callCount,
                    price.Input,
                    price.Output,
                    HasPrice(modelId),
                    CostFor(modelId, promptTokens, completionTokens));
            })
            .OrderByDescending(x => x.Cost)
            .ThenByDescending(x => x.TotalTokens)
            .ToList();

        var totalTokens = models.Sum(x => x.TotalTokens);
        var totalPrompt = models.Sum(x => x.PromptTokens);
        var totalCompletion = models.Sum(x => x.CompletionTokens);
        var totalCalls = models.Sum(x => x.CallCount);
        var totalCost = models.Sum(x => x.Cost);

        // ----- Theo tháng (12 tháng gần nhất); rows ngoài cửa sổ tự bị loại vì vòng lặp chỉ duyệt 12 tháng -----
        var monthly = Enumerable.Range(0, MonthsToShow)
            .Select(offset =>
            {
                var month = firstMonth.AddMonths(offset);
                var rows = logRaw.Where(x => x.Year == month.Year && x.Month == month.Month).ToList();
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

        // ----- Theo project (gộp lại từ logRaw) -----
        var projects = logRaw
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
            projects);
    }
}
