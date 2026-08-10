using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;

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

    /// <summary>
    /// System prompt for the native function-calling path: it carries no JSON-action contract and no tool
    /// list/schema in the text — the tools (and their schemas) are advertised to the model through the
    /// API's "tools" parameter instead.
    /// </summary>
    public string BuildNative(Agent agent)
    {
        // Chỉ MỘT placeholder cho vai: Agent không có cột Name riêng, nên trước đây {{agentName}} và
        // {{roleTitle}} cùng nhận RoleKey.GetTitle() và prompt render ra "You are Developer - Developer.".
        return _promptTemplateService.Get("Shared/tool-agent-native.v1.md")
            .Replace("{{roleTitle}}", agent.RoleKey.GetTitle())
            .Replace("{{instruction}}", _instructionProvider.GetInstruction(agent));
    }
}
