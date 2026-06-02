using ICOGenerator.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public class GetCallLogDetailQuery
{
    private readonly IAppDbContext _db;

    public GetCallLogDetailQuery(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<object?> ExecuteAsync(Guid id)
    {
        var log = await _db.AgentModelCallLogs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (log == null)
            return null;

        return new
        {
            log.Id,
            log.ProjectId,
            log.AgentId,
            log.AgentName,
            log.ModelName,
            log.ModelId,
            log.Endpoint,
            log.Purpose,
            log.Step,
            log.RequestJson,
            log.ResponseText,
            log.ExtractedContent,
            log.ErrorMessage,
            log.PromptTokens,
            log.CompletionTokens,
            log.TotalTokens,
            log.DurationMs,
            log.HttpStatusCode,
            log.IsSuccess,
            log.CreatedAt
        };
    }
}
