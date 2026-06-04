namespace ICOGenerator.Services.Tools.Registry;

public interface IToolRegistry
{
    Task<IReadOnlyList<ToolRuntimeDescriptor>> GetToolsForAgentAsync(Guid agentId);
}
