using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Một thông báo in-app gửi tới MỘT người dùng (theo <see cref="RecipientUsername"/>). Được sinh khi
/// một <see cref="WorkflowRun"/> chuyển trạng thái đáng chú ý — chờ duyệt tại cổng, hoàn tất, hoặc thất
/// bại — để người có quyền không phải canh Agent Dashboard mới biết có việc cần xử lý.
/// Mỗi người nhận đủ điều kiện có một dòng riêng (trạng thái đã đọc theo từng người). Xem NotificationService.
/// </summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Username người nhận (khớp <see cref="AppUser.Username"/>). Inbox lọc theo cột này.</summary>
    public string RecipientUsername { get; set; } = string.Empty;

    public NotificationType Type { get; set; }

    /// <summary>Tiêu đề ngắn hiển thị đậm ở dòng thông báo.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Nội dung phụ (tên bước / thông điệp).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Project liên quan (để lọc / điều hướng). Null nếu thông báo không gắn project.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>Chụp tên project tại thời điểm tạo để hiển thị mà không phải join.</summary>
    public string? ProjectName { get; set; }

    /// <summary>Workflow run liên quan (nếu có) — dùng để gom/đối chiếu, không khai báo FK.</summary>
    public Guid? WorkflowRunId { get; set; }

    /// <summary>URL tương đối để mở khi bấm vào thông báo (thường là Agent Dashboard của project).</summary>
    public string? Link { get; set; }

    public bool IsRead { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Thời điểm người nhận đọc (null nếu chưa đọc).</summary>
    public DateTime? ReadAt { get; set; }
}
