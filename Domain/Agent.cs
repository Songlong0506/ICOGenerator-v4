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
    public string? CreatedByUsername { get; set; }
    public ICollection<AgentTool> AgentTools { get; set; } = new List<AgentTool>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
