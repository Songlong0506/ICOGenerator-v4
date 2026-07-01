using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Một phản hồi người dùng gửi qua màn hình Feedback: báo lỗi, góp ý hoặc chia sẻ trải nghiệm dùng app.
/// Không gắn với project nào (phản hồi ở phạm vi toàn ứng dụng). File đính kèm (ảnh/PDF/doc/video) lưu trên
/// đĩa và metadata nằm ở <see cref="Attachments"/>.
/// </summary>
public class Feedback
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public FeedbackType Type { get; set; } = FeedbackType.Bug;

    public FeedbackStatus Status { get; set; } = FeedbackStatus.New;

    /// <summary>Tiêu đề ngắn gọn (bắt buộc).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Nội dung chi tiết (bắt buộc).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Username người gửi (claim Name) — dùng để lọc "phản hồi của tôi". Null nếu không xác định.</summary>
    public string? CreatedByUsername { get; set; }

    /// <summary>Tên hiển thị người gửi tại thời điểm gửi (chụp lại để không phụ thuộc việc đổi tên sau này).</summary>
    public string? SubmittedByName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Lần cập nhật trạng thái gần nhất (null nếu chưa ai triage).</summary>
    public DateTime? UpdatedAt { get; set; }

    public List<FeedbackAttachment> Attachments { get; set; } = new();
}
