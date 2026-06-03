using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Application.Agents;

public sealed record UpdateAgentCommand(
    Guid Id,
    string Name,
    string RoleTitle,
    string Description,
    string Instruction,
    string Color,
    AgentStatus Status,
    double Temperature,
    Guid? AiModelId,
    IReadOnlyCollection<Guid> ToolDefinitionIds);
