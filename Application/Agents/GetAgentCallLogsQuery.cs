using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public class GetAgentCallLogsQuery
{
    private readonly AppDbContext _db;

    public GetAgentCallLogsQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<object> ExecuteAsync(Guid projectId, Guid agentId)
    {
        return await _db.AgentModelCallLogs
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.AgentId == agentId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
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
                x.CreatedAt
            })
            .ToListAsync();
    }
}
