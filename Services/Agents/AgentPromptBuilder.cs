using ICOGenerator.Domain;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Tools.Registry;
using ICOGenerator.Services.Tools.Abstractions;
using ICOGenerator.Services.Tools.Execution;

namespace ICOGenerator.Services.Agents;

public class AgentPromptBuilder
{
    private readonly PromptTemplateService _promptTemplateService;
    private readonly AgentInstructionProvider _instructionProvider;

    public AgentPromptBuilder(PromptTemplateService promptTemplateService, AgentInstructionProvider instructionProvider)
    {
        _promptTemplateService = promptTemplateService;
        _instructionProvider = instructionProvider;
    }

    public string Build(Agent agent, IReadOnlyList<ToolRuntimeDescriptor> tools)
    {
        var toolText = string.Join("\n", tools.Select(t =>
            $"- {t.Definition.Name}: {t.Definition.Description}. InputSchema: {ToolSchemaBuilder.BuildInputSchema(t.Method)}"));

        return _promptTemplateService.Get("Agents/tool-agent.v1.md")
            .Replace("{{agentName}}", agent.Name)
            .Replace("{{roleTitle}}", agent.RoleTitle)
            .Replace("{{instruction}}", _instructionProvider.GetInstruction(agent))
            .Replace("{{tools}}", toolText);
    }
}
