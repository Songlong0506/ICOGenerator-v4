using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

// One row in an agent's AI call-log list (serialized to camelCase JSON for the Manage Agent popup).
public record AgentCallLogListItem(
    Guid Id, string AgentName, string ModelId, string Purpose,
    int Step, int PromptTokens, int CompletionTokens, int TotalTokens, long DurationMs,
    int? HttpStatusCode, bool IsSuccess, string? ErrorMessage, DateTime CreatedAt);

// A single page of an agent's AI call logs (mirrors ProjectListPage so the popup pager
// can share the same paging metadata/style as the Projects table).
// Purposes: distinct purpose values across the agent's logs (unfiltered) — feeds the popup's
// Purpose filter dropdown so it stays complete no matter which filters are currently applied.
public record AgentCallLogPage(
    IReadOnlyList<AgentCallLogListItem> Items,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<string> Purposes)
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

    // Filters (all optional) map to the popup's filter bar: Time (from/to), Purpose, Duration
    // (min/max ms) and Status. Times arrive as UTC wall-clock (CreatedAt is stored in UTC), so
    // they compare directly against the column.
    public async Task<AgentCallLogPage> ExecuteAsync(
        Guid projectId, Guid agentId, int page = 1, int pageSize = DefaultPageSize,
        string? purpose = null, string? status = null,
        long? minDurationMs = null, long? maxDurationMs = null,
        DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = DefaultPageSize;

        var scope = _db.AgentModelCallLogs
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.AgentId == agentId);

        // Distinct purposes across all of this agent's logs (before filters) for the dropdown.
        var purposes = await scope
            .Where(x => x.Purpose != "")
            .Select(x => x.Purpose)
            .Distinct()
            .OrderBy(p => p)
            .ToListAsync();

        var filtered = scope;

        if (!string.IsNullOrWhiteSpace(purpose))
            filtered = filtered.Where(x => x.Purpose == purpose);

        if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(x => x.IsSuccess);
        else if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(x => !x.IsSuccess);

        if (minDurationMs.HasValue)
            filtered = filtered.Where(x => x.DurationMs >= minDurationMs.Value);
        if (maxDurationMs.HasValue)
            filtered = filtered.Where(x => x.DurationMs <= maxDurationMs.Value);

        if (fromUtc.HasValue)
        {
            var from = DateTime.SpecifyKind(fromUtc.Value, DateTimeKind.Unspecified);
            filtered = filtered.Where(x => x.CreatedAt >= from);
        }
        if (toUtc.HasValue)
        {
            var to = DateTime.SpecifyKind(toUtc.Value, DateTimeKind.Unspecified);
            filtered = filtered.Where(x => x.CreatedAt <= to);
        }

        var baseQuery = filtered.OrderByDescending(x => x.CreatedAt);

        var totalCount = await baseQuery.CountAsync();

        var items = await baseQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AgentCallLogListItem(
                x.Id,
                x.AgentName,
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

        return new AgentCallLogPage(items, page, pageSize, totalCount, purposes);
    }
}
