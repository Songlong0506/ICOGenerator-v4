using ICOGenerator.Domain.Enums;
namespace ICOGenerator.Domain;
public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public AgentRoleKey RoleKey { get; set; } = AgentRoleKey.General;
    public string Description { get; set; } = string.Empty;
    public string Color { get; set; } = "#8B5CF6";
    public AgentStatus Status { get; set; } = AgentStatus.Active;
    public double Temperature { get; set; } = 0.3;
    // Mỗi agent bắt buộc gán AI model thủ công để tránh chạy nhầm model — không còn cơ chế model mặc định tự động.
    public Guid AiModelId { get; set; }
    public AiModel AiModel { get; set; } = null!;
    public ICollection<AgentTool> AgentTools { get; set; } = new List<AgentTool>();
    public ICollection<AgentModelCallLog> ModelCallLogs { get; set; } = new List<AgentModelCallLog>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
