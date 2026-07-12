using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum ResolveBriefCommentResult
{
    Resolved,
    NotFound,

    /// <summary>Đã resolve rồi — không làm gì thêm (idempotent về mặt dữ liệu).</summary>
    AlreadyResolved,

    /// <summary>Người gọi không phải tác giả/chủ project và không có quyền xem-tất-cả.</summary>
    NotAllowed
}

/// <summary>
/// Đánh dấu một góp ý Product Brief là đã xử lý. Được phép: tác giả góp ý, chủ project, hoặc người
/// có ProjectsViewAll (TeamDev/Admin). Góp ý resolve rồi vẫn giữ lại làm lịch sử — không xóa.
/// </summary>
public class ResolveBriefCommentUseCase
{
    private readonly AppDbContext _db;

    public ResolveBriefCommentUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ResolveBriefCommentResult> ExecuteAsync(
        Guid commentId, string? actorUsername, bool canManageAll, CancellationToken cancellationToken = default)
    {
        var comment = await _db.BriefComments
            .Include(c => c.Project)
            .FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);

        if (comment == null)
            return ResolveBriefCommentResult.NotFound;

        if (comment.ResolvedAt != null)
            return ResolveBriefCommentResult.AlreadyResolved;

        var isOwner = comment.Project?.CreatedByUsername != null && comment.Project.CreatedByUsername == actorUsername;
        if (!canManageAll && !isOwner && comment.AuthorUsername != actorUsername)
            return ResolveBriefCommentResult.NotAllowed;

        comment.ResolvedAt = DateTime.UtcNow;
        comment.ResolvedByUsername = actorUsername;

        await _db.SaveChangesAsync(cancellationToken);
        return ResolveBriefCommentResult.Resolved;
    }
}
