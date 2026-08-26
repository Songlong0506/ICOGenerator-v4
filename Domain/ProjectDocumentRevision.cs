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
    /// Mốc "input dẫn tới bản này": <see cref="AgentConversation"/> của lượt USER mới nhất trong project
    /// tại thời điểm ghi. Vòng soạn tài liệu chỉ chạy sau một LỆNH TƯỜNG MINH của người dùng
    /// (xem RequirementDraftTriggerCoverageTests) nên lượt đó chính là cú submit đứng sau bản ghi này —
    /// bấm "Write Requirement", gửi ghi chú đã ghim trên bản xem trước, hay chuyển phản hồi POC về.
    /// Hai mốc liền nhau khoanh đúng khoảng lượt user đã sinh ra thay đổi giữa hai revision, nên popup
    /// Lịch sử trả lời được "vì sao đổi" chứ không chỉ "đổi chỗ nào".
    /// CỐ Ý KHÔNG khai FK: lượt hội thoại bị xóa cứng ở đường retry (BAChatService) và bị lưu trữ ở
    /// "New Chat" — ràng buộc cascade sẽ kéo theo cả revision, tức xóa mất lịch sử tài liệu vì một thao
    /// tác trên khung chat. Mốc trỏ hụt thì đường đọc lùi về CreatedAt của revision.
    /// Null với các revision ghi trước khi có cột này.
    /// </summary>
    public Guid? TriggerConversationId { get; set; }

    /// <summary>
    /// VersionName của document TẠI THỜI ĐIỂM ghi (draft/V1/V2...). Giữ như nhãn lịch sử vì
    /// document draft được đổi tên thành V{n} khi Approve — revision cũ vẫn nhớ nó sinh ra lúc draft.
    /// </summary>
    public string VersionName { get; set; } = "draft";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
