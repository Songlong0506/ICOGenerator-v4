using ICOGenerator.Domain.Enums;
namespace ICOGenerator.Domain;
public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AgentRoleKey RoleKey { get; set; } = AgentRoleKey.BusinessAnalyst;
    public string Description { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public Guid AiModelId { get; set; }
    public AiModel AiModel { get; set; } = null!;
    // LEGACY — bucket CHUNG của "checklist học được" thời còn lưu dạng blob text. Bộ nhớ này nay nằm ở
    // AgentChecklistItem (mỗi bài học một dòng, có lý do + bật/tắt được); cột này chỉ còn là nguồn nhập
    // một lần lúc khởi động (ChecklistLegacyNotesImporter đọc rồi set null). Không ghi mới vào đây nữa.
    public string? LearnedChecklistNotes { get; set; }
    public string? CreatedByUsername { get; set; }
    public ICollection<AgentTool> AgentTools { get; set; } = new List<AgentTool>();
    public ICollection<AgentModelCallLog> ModelCallLogs { get; set; } = new List<AgentModelCallLog>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
