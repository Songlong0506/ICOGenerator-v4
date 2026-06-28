using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// File đính kèm của một <see cref="Feedback"/> (ảnh / PDF / tài liệu / video). File gốc lưu trên đĩa
/// (<see cref="StoredPath"/>); DB chỉ giữ metadata để hiển thị danh sách và phục vụ tải về.
/// </summary>
public class FeedbackAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FeedbackId { get; set; }
    public Feedback Feedback { get; set; } = default!;

    public FeedbackAttachmentKind Kind { get; set; }

    /// <summary>Tên file gốc do người dùng đặt (để hiển thị và làm tên khi tải về).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>MIME type (vd image/png, application/pdf, video/mp4) — dùng làm content-type khi trả file.</summary>
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Đường dẫn tuyệt đối tới file gốc đã lưu trên đĩa.</summary>
    public string StoredPath { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
