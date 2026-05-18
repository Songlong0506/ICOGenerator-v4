namespace ICOGenerator.Domain;
public class AgentTool
{
    public Guid AgentId { get; set; }
    public Agent Agent { get; set; } = default!;
    public Guid ToolDefinitionId { get; set; }
    public ToolDefinition ToolDefinition { get; set; } = default!;
}
