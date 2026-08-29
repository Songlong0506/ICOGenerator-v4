using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// MỘT mục trong "checklist BA học được" — đơn vị lưu trữ thật của bộ nhớ này.
///
/// <para>
/// Vì sao phải tách thành từng dòng: blob cũ được LLM VIẾT LẠI TOÀN BỘ sau mỗi vòng harvest, nên không
/// mục nào có định danh bền. Hệ quả là (a) người dùng xóa một bài học sai thì vòng sau học lại y hệt,
/// (b) không gắn được lý do/bằng chứng cho từng mục, (c) không thể có UI bật/tắt vì trạng thái tắt
/// không biết bám vào đâu. Mỗi mục là một dòng có <see cref="Id"/> ⇒ cả ba điều trên làm được.
/// </para>
///
/// <para>
/// Bucket của mục = <see cref="DepartmentCode"/> (null = bài học áp dụng cho MỌI dự án; ngược lại chỉ nạp
/// cho dự án của cùng phòng ban). Xem <see cref="Services.Requirements.ChecklistNoteStore"/>.
/// </para>
/// </summary>
public class AgentChecklistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentId { get; set; }
    public Agent Agent { get; set; } = default!;

    /// <summary>
    /// Mã phòng ban (<c>OrgUnits.OrgUnitCode</c> của một đơn vị <c>IsDepartment</c>) mà bài học thuộc về;
    /// null = bucket chung, áp dụng mọi dự án. Suy từ đơn vị yêu cầu của dự án nguồn — xem
    /// <see cref="Services.Requirements.ChecklistNoteStore.ResolveBucketAsync"/>.
    /// </summary>
    public string? DepartmentCode { get; set; }

    /// <summary>Nội dung mục checklist — ĐÚNG phần văn bản được nạp vào prompt BA, không kèm lý do.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Vì sao rút ra được bài học này (một câu, do vòng harvest ghi). Chỉ hiển thị trên trang quản trị,
    /// KHÔNG bao giờ đi vào prompt — nếu nhồi chung vào text thì mỗi lượt chat của mọi dự án cùng bucket
    /// phải trả token cho phần chỉ con người cần đọc. null khi vòng harvest không nêu được lý do.
    /// </summary>
    public string? Rationale { get; set; }

    /// <summary>Bằng chứng gốc (trích ngắn lượt hội thoại / ghi chú POC đã dẫn tới bài học). null nếu không truy được.</summary>
    public string? Evidence { get; set; }

    public ChecklistItemSource SourceKind { get; set; } = ChecklistItemSource.Conversation;

    /// <summary>Dự án đã sinh ra bài học này — null khi dự án đã bị xóa.</summary>
    public Guid? SourceProjectId { get; set; }

    public ChecklistItemStatus Status { get; set; } = ChecklistItemStatus.Active;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
