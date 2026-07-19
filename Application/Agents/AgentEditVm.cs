namespace ICOGenerator.Application.Agents;

public class AgentEditVm
{
    public Guid Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Color { get; set; } = "#8B5CF6";
    public double Temperature { get; set; } = 0.3;
    public Guid? AiModelId { get; set; }
    public List<Guid> ToolDefinitionIds { get; set; } = new();
}
