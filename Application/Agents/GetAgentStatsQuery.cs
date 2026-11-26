using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

// Per-agent usage figures for the dashboard table — kept deliberately small so the page can poll
// it on a short interval to keep Share / Total Tokens / Calls / Last Activity columns fresh without
// reloading the whole page (or re-enumerating the workspace, as GetAgentDashboardQuery does).
// GetAgentDashboardQuery cũng lấy số từ đây cho lần render đầu, nên hai đường không bao giờ lệch nhau.
//
// Bốn cột token tách phần prompt theo cache — cùng tên trường với một dòng call log
// (AgentCallLogListItem) để agent-dashboard.js dựng ô Total Tokens bằng đúng component của cột Tokens
// trong popup AI Call Logs (tokensCell): cached + cache write + uncached = prompt; prompt + completion = total.
public record AgentStatItem(
    Guid AgentId,
    long PromptTokens, long CachedPromptTokens, long CacheWritePromptTokens, long CompletionTokens, long TotalTokens,
    int Calls, DateTime? LastActivityUtc);

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
                Prompt = g.Sum(x => (long)x.PromptTokens),
                Cached = g.Sum(x => (long)x.CachedPromptTokens),
                CacheWrite = g.Sum(x => (long)x.CacheWritePromptTokens),
                Completion = g.Sum(x => (long)x.CompletionTokens),
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
                x.Prompt,
                x.Cached,
                x.CacheWrite,
                x.Completion,
                x.Total,
                x.Calls,
                DateTime.SpecifyKind(x.LastActivity, DateTimeKind.Utc)))
            .ToList();

        var totalTokens = statsByAgent.Sum(x => x.Total);

        return new AgentStatsResult(totalTokens, agents);
    }
}
