namespace ICOGenerator.Domain;

/// <summary>
/// Một PHIÊN BẢN nội dung của một template prompt (file .md dưới /Prompts). Prompt gốc vẫn là file
/// trong repo; bảng này giữ các bản chỉnh sửa runtime từ màn hình Prompt Studio: mỗi lần lưu là một
/// snapshot ĐẦY ĐỦ nội dung (không lưu delta — như ProjectDocumentRevision), đánh số tăng dần theo
/// <see cref="PromptKey"/>. Nhiều nhất MỘT bản <see cref="IsActive"/> cho mỗi key: bản đó được
/// PromptTemplateService dùng THAY nội dung file (xem IPromptOverrideProvider); không có bản active
/// ⇒ dùng file như trước. Nhờ vậy sửa prompt không cần deploy, rollback một cú nhấp, và lịch sử
/// "prompt đã từng là gì" không bao giờ mất.
/// </summary>
public class PromptTemplateVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Đường dẫn tương đối dưới /Prompts, separator '/' (vd "BA/requirement-chat.v3.md").</summary>
    public string PromptKey { get; set; } = string.Empty;

    /// <summary>Số phiên bản 1-based, tăng dần theo PromptKey (v1 thường là bản chụp từ file).</summary>
    public int VersionNumber { get; set; }

    /// <summary>Snapshot đầy đủ nội dung template ở phiên bản này.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Ghi chú của người sửa, vd "siết điều kiện mời bấm Write Requirement".</summary>
    public string? ChangeNote { get; set; }

    /// <summary>Bản đang dùng thay file. Bất biến: mỗi PromptKey có nhiều nhất MỘT bản active.</summary>
    public bool IsActive { get; set; }

    public string? CreatedByUsername { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
