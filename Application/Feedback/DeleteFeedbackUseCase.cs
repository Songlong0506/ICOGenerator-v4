using ICOGenerator.Data;
using ICOGenerator.Services.Feedback;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Feedback;

public enum DeleteFeedbackResult { Ok, NotFound, Forbidden }

/// <summary>
/// Xóa một phản hồi cùng file đính kèm. Người gửi xóa được phản hồi của mình; người có quyền FeedbackManage
/// xóa được mọi phản hồi. Xóa cả file trên đĩa và (qua cascade FK) các bản ghi attachment.
/// </summary>
public class DeleteFeedbackUseCase
{
    private readonly AppDbContext _db;
    private readonly FeedbackAttachmentStore _store;

    public DeleteFeedbackUseCase(AppDbContext db, FeedbackAttachmentStore store)
    {
        _db = db;
        _store = store;
    }

    public async Task<DeleteFeedbackResult> ExecuteAsync(
        Guid id, string? username, bool canManage, CancellationToken cancellationToken = default)
    {
        var feedback = await _db.Feedbacks.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (feedback == null)
            return DeleteFeedbackResult.NotFound;

        var isOwner = username != null && feedback.CreatedByUsername == username;
        if (!canManage && !isOwner)
            return DeleteFeedbackResult.Forbidden;

        _db.Feedbacks.Remove(feedback);
        await _db.SaveChangesAsync(cancellationToken);

        // Dọn file sau khi DB commit thành công (best-effort; file mồ côi không gây lỗi nghiệp vụ).
        _store.DeleteFiles(feedback.Id);
        return DeleteFeedbackResult.Ok;
    }
}
