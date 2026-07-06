namespace ICOGenerator.Domain;

/// <summary>
/// Một "ảnh chụp" nội dung của <see cref="ProjectDocument"/> tại MỖI lần nội dung bị ghi/ghi đè.
/// Tài liệu sinh ra bị ghi đè ở nhiều luồng (bấm lại "Write Requirement" trên draft, vòng
/// "Yêu cầu chỉnh sửa" sinh lại BRD/SRS/FSD/UserStories cùng phiên bản...) — không có bảng này
/// thì lịch sử mất sạch, không trả lời được "bản trước viết gì, lần sửa này đổi chỗ nào".
/// Quy ước: revision N giữ nội dung ĐẦY ĐỦ sau lần ghi thứ N (không lưu delta); bản mới nhất luôn
/// trùng với <see cref="ProjectDocument.Content"/>. Diff được tính lúc xem (DocumentDiffService).
/// </summary>
public class ProjectDocumentRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectDocumentId { get; set; }
    public ProjectDocument ProjectDocument { get; set; } = default!;

    /// <summary>Số thứ tự tăng dần từ 1 trong phạm vi một document.</summary>
    public int RevisionNumber { get; set; }

    /// <summary>Nội dung đầy đủ của tài liệu tại revision này.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Nguồn gốc thay đổi, vd "Write Requirement" hay "Chỉnh sửa theo nhận xét: ...".</summary>
    public string ChangeNote { get; set; } = string.Empty;

    /// <summary>
    /// VersionName của document TẠI THỜI ĐIỂM ghi (draft/V1/V2...). Giữ như nhãn lịch sử vì
    /// document draft được đổi tên thành V{n} khi Approve — revision cũ vẫn nhớ nó sinh ra lúc draft.
    /// </summary>
    public string VersionName { get; set; } = "draft";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
