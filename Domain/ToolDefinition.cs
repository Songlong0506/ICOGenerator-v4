namespace ICOGenerator.Domain;
public class ToolDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public ICollection<AgentTool> AgentTools { get; set; } = new List<AgentTool>();
}
