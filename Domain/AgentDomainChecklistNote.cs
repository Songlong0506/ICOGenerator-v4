namespace ICOGenerator.Domain;

/// <summary>
/// "Checklist học được" của một agent, TÁCH THEO MIỀN NGHIỆP VỤ (Project.DomainKey). Bucket chung
/// (bài học áp dụng cho mọi miền) vẫn nằm ở <see cref="Agent.LearnedChecklistNotes"/> — bảng này chỉ
/// chứa các bucket theo miền, để bài học của dự án kho không chiếm chỗ/không gây nhiễu khi BA phỏng
/// vấn dự án nghỉ phép (mỗi bucket có trần ký tự riêng thay vì mọi miền chen trong một cột 4000 ký tự).
/// Xem <see cref="Services.Requirements.ChecklistNoteStore"/>.
/// </summary>
public class AgentDomainChecklistNote
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentId { get; set; }
    public Agent Agent { get; set; } = default!;

    // Slug miền thuộc taxonomy cố định của ProjectDomainClassifier (vd "leave-management").
    public string DomainKey { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
