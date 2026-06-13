using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Usage;

public record MonthlyUsageItem(int Year, int Month, long PromptTokens, long CompletionTokens, long TotalTokens, int CallCount);

public record ProjectUsageItem(Guid ProjectId, string ProjectName, long PromptTokens, long CompletionTokens, long TotalTokens, int CallCount, DateTime? LastCallAt);

public record UsageOverviewVm(
    long TotalTokens,
    long TotalPromptTokens,
    long TotalCompletionTokens,
    int TotalCalls,
    long CurrentMonthTokens,
    IReadOnlyList<MonthlyUsageItem> MonthlyUsage,
    IReadOnlyList<ProjectUsageItem> ProjectUsage);

public class GetUsageOverviewQuery
{
    private const int MonthsToShow = 12;

    private readonly AppDbContext _db;
    public GetUsageOverviewQuery(AppDbContext db) => _db = db;

    public async Task<UsageOverviewVm> ExecuteAsync()
    {
        var totals = await _db.AgentModelCallLogs
            .GroupBy(_ => 1)
            .Select(g => new
            {
                PromptTokens = g.Sum(x => (long)x.PromptTokens),
                CompletionTokens = g.Sum(x => (long)x.CompletionTokens),
                TotalTokens = g.Sum(x => (long)x.TotalTokens),
                CallCount = g.Count()
            })
            .FirstOrDefaultAsync();

        var now = DateTime.UtcNow;
        var firstMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(MonthsToShow - 1));

        var monthlyRaw = await _db.AgentModelCallLogs
            .Where(x => x.CreatedAt >= firstMonth)
            .GroupBy(x => new { x.CreatedAt.Year, x.CreatedAt.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
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
                var found = monthlyRaw.FirstOrDefault(x => x.Year == month.Year && x.Month == month.Month);
                return new MonthlyUsageItem(
                    month.Year,
                    month.Month,
                    found?.PromptTokens ?? 0,
                    found?.CompletionTokens ?? 0,
                    found?.TotalTokens ?? 0,
                    found?.CallCount ?? 0);
            })
            .ToList();

        var projectRaw = await _db.AgentModelCallLogs
            .AsNoTracking()
            .GroupBy(x => new
            {
                x.ProjectId,
                ProjectName = x.Project!.Name
            })
            .Select(g => new
            {
                g.Key.ProjectId,
                g.Key.ProjectName,
                PromptTokens = g.Sum(x => (long)x.PromptTokens),
                CompletionTokens = g.Sum(x => (long)x.CompletionTokens),
                TotalTokens = g.Sum(x => (long)x.TotalTokens),
                CallCount = g.Count(),
                LastCalledAt = g.Max(x => (DateTime?)x.CreatedAt)
            })
            .OrderByDescending(x => x.TotalTokens)
            .ToListAsync();

        var projects = projectRaw
            .Select(x => new ProjectUsageItem(
                x.ProjectId,
                x.ProjectName,
                x.PromptTokens,
                x.CompletionTokens,
                x.TotalTokens,
                x.CallCount,
                x.LastCalledAt))
            .ToList();

        return new UsageOverviewVm(
            totals?.TotalTokens ?? 0,
            totals?.PromptTokens ?? 0,
            totals?.CompletionTokens ?? 0,
            totals?.CallCount ?? 0,
            monthly[^1].TotalTokens,
            monthly,
            projects);
    }
}
