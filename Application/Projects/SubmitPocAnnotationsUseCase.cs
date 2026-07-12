using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum SubmitPocAnnotationsResult
{
    Submitted,
    ProjectNotFound,

    /// <summary>Không có annotation nào còn mở để gửi.</summary>
    NothingToSubmit
}

/// <summary>
/// "Gửi phản hồi cho đội Dev": chuyển MỌI annotation Open của project sang Submitted và bắn thông báo
/// tới những người có quyền DeliveryAdvance (link mở thẳng trang POC Review). Gửi cả lô — một vòng review
/// có thể có nhiều người góp ý, đội Dev nên thấy trọn gói thay vì từng mảnh. Việc biến các góp ý thành
/// yêu cầu chỉnh sửa POC cho agent là bước riêng của đội Dev (ApplyPocAnnotationsRevisionUseCase).
/// </summary>
public class SubmitPocAnnotationsUseCase
{
    private readonly AppDbContext _db;
    private readonly INotificationService _notifications;

    public SubmitPocAnnotationsUseCase(AppDbContext db, INotificationService notifications)
    {
        _db = db;
        _notifications = notifications;
    }

    public async Task<SubmitPocAnnotationsResult> ExecuteAsync(Guid projectId, string? actorUsername, CancellationToken cancellationToken = default)
    {
        var projectExists = await _db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken);
        if (!projectExists)
            return SubmitPocAnnotationsResult.ProjectNotFound;

        var openAnnotations = await _db.PocAnnotations
            .Where(a => a.ProjectId == projectId && a.Status == PocAnnotationStatus.Open)
            .ToListAsync(cancellationToken);

        if (openAnnotations.Count == 0)
            return SubmitPocAnnotationsResult.NothingToSubmit;

        var now = DateTime.UtcNow;
        foreach (var annotation in openAnnotations)
        {
            annotation.Status = PocAnnotationStatus.Submitted;
            annotation.SubmittedAt = now;
        }

        // NotificationService chỉ Add vào change tracker (không SaveChanges) — lưu atomic cùng trạng thái
        // annotation ở SaveChanges dưới. Fail-open bên trong service: lỗi thông báo không chặn việc gửi.
        await _notifications.NotifyPocFeedbackSubmittedAsync(projectId, actorUsername, openAnnotations.Count, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        return SubmitPocAnnotationsResult.Submitted;
    }
}
