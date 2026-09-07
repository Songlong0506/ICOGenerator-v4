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
}
