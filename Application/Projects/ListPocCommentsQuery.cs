using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>
/// Một ghi chú ghim trên POC, ở dạng client render được. CanDelete tính sẵn phía server (chủ ghi chú
/// hoặc người có DeliveryAdvance) để JS không phải đoán quyền.
/// </summary>
public record PocCommentItem(
    Guid Id,
    string PageView,
    string ElementLabel,
    string ElementPath,
    double XPercent,
    double YPercent,
    string Comment,
    string Status,
    string? CreatedBy,
    DateTime CreatedAt,
    bool CanDelete);

public class ListPocCommentsQuery
{
    private readonly AppDbContext _db;

    public ListPocCommentsQuery(AppDbContext db)
    {
        _db = db;
    }

    /// <param name="currentUsername">User đang xem — quyết định CanDelete cho ghi chú của chính họ.</param>
    /// <param name="canManage">True khi user có DeliveryAdvance (xóa được mọi ghi chú).</param>
    public async Task<List<PocCommentItem>> ExecuteAsync(
        Guid projectId, string? currentUsername, bool canManage, CancellationToken cancellationToken = default)
    {
        var comments = await _db.PocComments.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return comments.Select(x => new PocCommentItem(
            x.Id,
            x.PageView,
            x.ElementLabel,
            x.ElementPath,
            x.XPercent,
            x.YPercent,
            x.Comment,
            x.Status.ToString(),
            x.CreatedByUsername,
            x.CreatedAt,
            canManage || (currentUsername != null && x.CreatedByUsername == currentUsername)))
            .ToList();
    }
}
