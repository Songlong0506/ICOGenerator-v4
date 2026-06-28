using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Feedback;

public enum UpdateFeedbackStatusResult { Ok, NotFound }

/// <summary>
/// Cập nhật trạng thái triage của một phản hồi. Chỉ dành cho người có quyền FeedbackManage (controller chặn).
/// </summary>
public class UpdateFeedbackStatusUseCase
{
    private readonly AppDbContext _db;

    public UpdateFeedbackStatusUseCase(AppDbContext db) => _db = db;

    public async Task<UpdateFeedbackStatusResult> ExecuteAsync(
        Guid id, FeedbackStatus status, CancellationToken cancellationToken = default)
    {
        var feedback = await _db.Feedbacks.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (feedback == null)
            return UpdateFeedbackStatusResult.NotFound;

        feedback.Status = status;
        feedback.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return UpdateFeedbackStatusResult.Ok;
    }
}
