using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>
/// Xóa một ghi chú ghim trên POC. Cùng quy tắc sở hữu với Feedback: chủ ghi chú xóa được của mình,
/// người có DeliveryAdvance (người duyệt cổng) xóa được mọi ghi chú của project.
/// </summary>
public class DeletePocCommentUseCase
{
    private readonly AppDbContext _db;

    public DeletePocCommentUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> ExecuteAsync(Guid id, string? currentUsername, bool canManage, CancellationToken cancellationToken = default)
    {
        var comment = await _db.PocComments.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (comment == null)
            return false;

        if (!canManage && (currentUsername == null || comment.CreatedByUsername != currentUsername))
            return false;

        _db.PocComments.Remove(comment);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
