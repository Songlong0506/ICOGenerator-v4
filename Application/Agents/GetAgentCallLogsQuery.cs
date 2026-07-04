using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

// One row in an agent's AI call-log list (serialized to camelCase JSON for the Manage Agent popup).
public record AgentCallLogListItem(
    Guid Id, string AgentName, string ModelName, string ModelId, string Purpose,
    int Step, int PromptTokens, int CompletionTokens, int TotalTokens, long DurationMs,
    int? HttpStatusCode, bool IsSuccess, string? ErrorMessage, DateTime CreatedAt);

// A single page of an agent's AI call logs (mirrors ProjectListPage so the popup pager
// can share the same paging metadata/style as the Projects table).
public record AgentCallLogPage(
    IReadOnlyList<AgentCallLogListItem> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
    public int FirstItemIndex => TotalCount == 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int LastItemIndex => Math.Min(Page * PageSize, TotalCount);
}

public class GetAgentCallLogsQuery
{
    public const int DefaultPageSize = 15;

    private readonly AppDbContext _db;

    public GetAgentCallLogsQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AgentCallLogPage> ExecuteAsync(
        Guid projectId, Guid agentId, int page = 1, int pageSize = DefaultPageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = DefaultPageSize;

        var baseQuery = _db.AgentModelCallLogs
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.AgentId == agentId)
            .OrderByDescending(x => x.CreatedAt);

        var totalCount = await baseQuery.CountAsync();

        var items = await baseQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AgentCallLogListItem(
                x.Id,
                x.AgentName,
                x.ModelName,
                x.ModelId,
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

        return new AgentCallLogPage(items, page, pageSize, totalCount);
    }
}
