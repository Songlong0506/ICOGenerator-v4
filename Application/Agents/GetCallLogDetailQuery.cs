using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

// Full detail of a single AI call log (request/response payloads included), for the call-log detail
// modal. Serialized to camelCase JSON; property names mirror the columns the frontend reads.
public record CallLogDetailVm(
    Guid Id, Guid ProjectId, Guid AgentId, string AgentName, string ModelName, string ModelId,
    string Endpoint, string Purpose, int Step, string RequestJson, string ResponseText,
    string? ExtractedContent, string? ErrorMessage, int PromptTokens, int CompletionTokens,
    int TotalTokens, long DurationMs, int? HttpStatusCode, bool IsSuccess, DateTime CreatedAt);

public class GetCallLogDetailQuery
{
    private readonly AppDbContext _db;

    public GetCallLogDetailQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<CallLogDetailVm?> ExecuteAsync(Guid id)
    {
        var log = await _db.AgentModelCallLogs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (log == null)
            return null;

        return new CallLogDetailVm(
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
            log.CreatedAt);
    }
}
