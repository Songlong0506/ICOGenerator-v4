using ICOGenerator.Domain.Enums;
namespace ICOGenerator.Domain;
public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public AgentRoleKey RoleKey { get; set; } = AgentRoleKey.BusinessAnalyst;
    public string Description { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public AgentStatus Status { get; set; } = AgentStatus.Active;
    public double Temperature { get; set; }
    public Guid AiModelId { get; set; }
    public AiModel AiModel { get; set; } = null!;
    // Checklist bổ sung mà BA tự rút kinh nghiệm XUYÊN SUỐT mọi dự án/mọi người dùng (khác AppUser.UserMemory
    // vốn gắn theo TỪNG người dùng): mỗi khi một dự án hoàn tất mà người dùng phải tự nêu ra thông tin BA
    // chưa từng hỏi tới, khoảng trống đó được khái quát hoá và gộp vào đây, rồi nạp lại cho MỌI dự án MỚI để
    // BA hỏi kỹ hơn ngay từ đầu. null = chưa rút được kinh nghiệm nào. Xem ChecklistGapMemoryService.
    public string? LearnedChecklistNotes { get; set; }
    public string? CreatedByUsername { get; set; }
    public ICollection<AgentTool> AgentTools { get; set; } = new List<AgentTool>();
    public ICollection<AgentModelCallLog> ModelCallLogs { get; set; } = new List<AgentModelCallLog>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
