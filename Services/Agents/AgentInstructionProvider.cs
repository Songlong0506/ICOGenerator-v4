using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Prompts;

namespace ICOGenerator.Services.Agents;

/// <summary>
/// Resolves an agent's instruction from Prompts/Agents/Instructions/{RoleKey}.md instead of a database column.
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
            // Fall back to the General instruction; if even that is missing, no instruction.
            if (roleKey == AgentRoleKey.General)
                return string.Empty;

            return GetInstruction(AgentRoleKey.General);
        }
    }

    public static string RelativePath(AgentRoleKey roleKey) => $"Agents/Instructions/{roleKey}.md";
}
