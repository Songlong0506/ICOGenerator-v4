using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

// Per-agent usage figures for the dashboard table — kept deliberately small so the page can poll
// it on a short interval to keep Share / Total Tokens / Calls / Last Activity columns fresh without
// reloading the whole page (or re-enumerating the workspace, as GetAgentDashboardQuery does).
public record AgentStatItem(Guid AgentId, long TotalTokens, int Calls, DateTime? LastActivityUtc);

public record AgentStatsResult(long TotalTokens, IReadOnlyList<AgentStatItem> Agents);

public class GetAgentStatsQuery
{
    private readonly AppDbContext _db;

    public GetAgentStatsQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AgentStatsResult> ExecuteAsync(Guid projectId)
    {
        var statsByAgent = await _db.AgentModelCallLogs.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .GroupBy(x => x.AgentId)
            .Select(g => new
            {
                AgentId = g.Key,
                Total = g.Sum(x => (long)x.TotalTokens),
                Calls = g.Count(),
                LastActivity = g.Max(x => x.CreatedAt)
            })
            .ToListAsync();

        // Timestamps are persisted as UTC; tag the kind explicitly so JSON serializes a "Z" suffix and
        // the browser renders it in the viewer's local timezone (same contract as the server-rendered cells).
        var agents = statsByAgent
            .Select(x => new AgentStatItem(
                x.AgentId,
                x.Total,
                x.Calls,
                DateTime.SpecifyKind(x.LastActivity, DateTimeKind.Utc)))
            .ToList();

        var totalTokens = statsByAgent.Sum(x => x.Total);

        return new AgentStatsResult(totalTokens, agents);
    }
}
