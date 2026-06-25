using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

// One row in an agent's AI call-log list (serialized to camelCase JSON for the Manage Agent popup).
public record AgentCallLogListItem(
    Guid Id, string AgentName, string ModelName, string ModelId, string Endpoint, string Purpose,
    int Step, int PromptTokens, int CompletionTokens, int TotalTokens, long DurationMs,
    int? HttpStatusCode, bool IsSuccess, string? ErrorMessage, DateTime CreatedAt);

public class GetAgentCallLogsQuery
{
    private readonly AppDbContext _db;

    public GetAgentCallLogsQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<AgentCallLogListItem>> ExecuteAsync(Guid projectId, Guid agentId)
    {
        return await _db.AgentModelCallLogs
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.AgentId == agentId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new AgentCallLogListItem(
                x.Id,
                x.AgentName,
                x.ModelName,
                x.ModelId,
                x.Endpoint,
                x.Purpose,
                x.Step,
                x.PromptTokens,
                x.CompletionTokens,
                x.TotalTokens,
                x.DurationMs,
                x.HttpStatusCode,
                x.IsSuccess,
                x.ErrorMessage,
                x.CreatedAt))
            .ToListAsync();
    }
}
