using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Application.Agents;

public class AgentEditVm
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RoleTitle { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Color { get; set; } = "#8B5CF6";
    public AgentStatus Status { get; set; } = AgentStatus.Active;
    public double Temperature { get; set; } = 0.3;
    public Guid? AiModelId { get; set; }
    public List<Guid> ToolDefinitionIds { get; set; } = new();
}
