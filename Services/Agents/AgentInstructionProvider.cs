using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;

namespace ICOGenerator.Services.Agents;

/// <summary>
/// Resolves an agent's instruction from Prompts/{RoleKey}/instruction.md instead of a database column.
/// </summary>
public class AgentInstructionProvider
{
    private readonly PromptTemplateService _promptTemplateService;

    public AgentInstructionProvider(PromptTemplateService promptTemplateService)
    {
        _promptTemplateService = promptTemplateService;
    }

    public string GetInstruction(Agent agent) => GetInstruction(agent.RoleKey);

    public string GetInstruction(AgentRoleKey roleKey)
    {
        try
        {
            return _promptTemplateService.Get(RelativePath(roleKey)).Trim();
        }
        catch (FileNotFoundException)
        {
            // No instruction file for this role: run without a role-specific instruction.
            return string.Empty;
        }
    }

    public static string RelativePath(AgentRoleKey roleKey) => $"{roleKey}/instruction.md";
}
