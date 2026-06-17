using ICOGenerator.Domain;

namespace ICOGenerator.Application.Agents;

public record AgentManagementPage(
    IReadOnlyList<Agent> Agents,
    Agent? SelectedAgent,
    IReadOnlyList<AiModel> Models,
    IReadOnlyList<ToolDefinition> Tools,
    string Instruction,
    string InstructionFile);
