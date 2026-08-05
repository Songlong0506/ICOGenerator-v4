using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// MỘT mục trong "checklist BA học được" — đơn vị lưu trữ thật của bộ nhớ này, thay cho blob text cũ
/// (<see cref="Agent.LearnedChecklistNotes"/> / <see cref="AgentDomainChecklistNote"/>).
///
/// <para>
/// Vì sao phải tách thành từng dòng: blob cũ được LLM VIẾT LẠI TOÀN BỘ sau mỗi vòng harvest, nên không
/// mục nào có định danh bền. Hệ quả là (a) người dùng xóa một bài học sai thì vòng sau học lại y hệt,
/// (b) không gắn được lý do/bằng chứng cho từng mục, (c) không thể có UI bật/tắt vì trạng thái tắt
/// không biết bám vào đâu. Mỗi mục là một dòng có <see cref="Id"/> ⇒ cả ba điều trên làm được.
/// </para>
///
/// <para>
/// Bucket của mục = <see cref="DomainKey"/> (null = bài học áp dụng cho MỌI dự án; ngược lại chỉ nạp cho
/// dự án cùng miền nghiệp vụ). Xem <see cref="Services.Requirements.ChecklistNoteStore"/>.
/// </para>
/// </summary>
public class AgentChecklistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentId { get; set; }
    public Agent Agent { get; set; } = default!;

    /// <summary>Slug miền thuộc taxonomy của ProjectDomainClassifier (vd "leave-management"); null = bucket chung.</summary>
    public string? DomainKey { get; set; }

    /// <summary>Nội dung mục checklist — ĐÚNG phần văn bản được nạp vào prompt BA, không kèm lý do.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Vì sao rút ra được bài học này (một câu, do vòng harvest ghi). Chỉ hiển thị trên trang quản trị,
    /// KHÔNG bao giờ đi vào prompt — nếu nhồi chung vào text thì mỗi lượt chat của mọi dự án cùng bucket
    /// phải trả token cho phần chỉ con người cần đọc. null với mục nhập từ blob cũ.
    /// </summary>
    public string? Rationale { get; set; }

    /// <summary>Bằng chứng gốc (trích ngắn lượt hội thoại / ghi chú POC đã dẫn tới bài học). null nếu không truy được.</summary>
    public string? Evidence { get; set; }

    public ChecklistItemSource SourceKind { get; set; } = ChecklistItemSource.Conversation;

    /// <summary>Dự án đã sinh ra bài học này — null khi dự án đã bị xóa hoặc mục đến từ blob cũ.</summary>
    public Guid? SourceProjectId { get; set; }

    public ChecklistItemStatus Status { get; set; } = ChecklistItemStatus.Active;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
