namespace ICOGenerator.Application.Agents;

public class AgentEditVm
{
    public Guid Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Color { get; set; } = "#8B5CF6";
    public Guid? AiModelId { get; set; }

    /// <summary>Model riêng cho lượt trọng tài của agent BA; null = dùng <see cref="AiModelId"/>.</summary>
    public Guid? JudgeAiModelId { get; set; }
    public List<Guid> ToolDefinitionIds { get; set; } = new();
}
