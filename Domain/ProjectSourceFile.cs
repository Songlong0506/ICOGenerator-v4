using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Tài liệu nguồn (ảnh / PDF) người dùng upload vào một project để agent BA dùng làm ngữ cảnh khi chat
/// và khi sinh tài liệu requirement. Khác với <see cref="ProjectDocument"/> (vốn là OUTPUT đã sinh):
/// đây là INPUT do người dùng cung cấp. File gốc lưu trên đĩa workspace (<see cref="StoredPath"/>); DB chỉ
/// giữ metadata + phần text đã bóc (<see cref="ExtractedText"/>) và đường dẫn các ảnh trang scan đã render.
/// </summary>
public class ProjectSourceFile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;

    public SourceFileKind Kind { get; set; }

    /// <summary>Tên file gốc do người dùng đặt (để hiển thị).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>MIME type (vd image/png, application/pdf) — dùng làm media type khi gửi cho model vision.</summary>
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Đường dẫn tuyệt đối tới file gốc đã lưu trong workspace project.</summary>
    public string StoredPath { get; set; } = string.Empty;

    /// <summary>Text bóc từ PDF (null với ảnh, hoặc PDF scan/ảnh không có text — loại này không được hỗ trợ).</summary>
    public string? ExtractedText { get; set; }

    /// <summary>
    /// [LEGACY] Trước đây giữ JSON list đường dẫn ảnh PNG của các trang PDF scan đã render cho vision. App đã bỏ
    /// render PDF→ảnh nên trường này KHÔNG còn được ghi cho nguồn mới (luôn null); giữ cột để tương thích dữ liệu cũ.
    /// </summary>
    public string? PageImagePaths { get; set; }

    /// <summary>Số trang (với PDF). 0 với ảnh.</summary>
    public int PageCount { get; set; }

    /// <summary>True nếu nguồn này có phần ảnh cần model vision (chỉ với file ảnh upload trực tiếp; PDF luôn false).</summary>
    public bool IsVisionSource { get; set; }

    public string? UploadedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
