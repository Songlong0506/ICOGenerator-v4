namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Phân loại file đính kèm phản hồi để hiển thị icon/nhóm phù hợp (ảnh xem trực tiếp, còn lại tải về).
/// </summary>
public enum FeedbackAttachmentKind
{
    Image = 1,
    Pdf = 2,
    Document = 3,
    Video = 4,
    Other = 5
}
