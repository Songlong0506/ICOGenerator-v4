using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Application.Audit;

public record AuditLogListPage(
    IReadOnlyList<AuditLog> Items,
    AuditCategory? CategoryFilter,
    int Page,
    int PageSize,
    int TotalCount,
    // Tên hiển thị của người thực hiện, tra từ AuditLog.ActorUsername → AppUser.DisplayName. Username
    // không có trong dict (user không còn / DisplayName trống) ⇒ view fallback về chính username.
    IReadOnlyDictionary<string, string> ActorDisplayNames)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
    public int FirstItemIndex => TotalCount == 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int LastItemIndex => Math.Min(Page * PageSize, TotalCount);
}
