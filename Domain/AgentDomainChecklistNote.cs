namespace ICOGenerator.Domain;

/// <summary>
/// LEGACY — kho cũ của "checklist học được" theo miền nghiệp vụ, dạng BLOB text một cột. Bộ nhớ này nay
/// lưu theo từng bài học (<see cref="AgentChecklistItem"/>) để mỗi mục có định danh bền, có lý do và
/// bật/tắt được; bảng này chỉ còn là NGUỒN NHẬP một lần lúc khởi động
/// (<see cref="Services.Requirements.ChecklistLegacyNotesImporter"/> đọc rồi xóa dòng đã nhập), giữ lại
/// để dữ liệu đã học của các bản cài cũ không mất. Không đường nào ghi mới vào đây nữa.
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
