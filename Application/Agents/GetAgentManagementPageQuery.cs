using ICOGenerator.Data;
using ICOGenerator.Services.Agents;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public class GetAgentManagementPageQuery
{
    private readonly AppDbContext _db;
    private readonly AgentInstructionProvider _instructionProvider;

    public GetAgentManagementPageQuery(AppDbContext db, AgentInstructionProvider instructionProvider)
    {
        _db = db;
        _instructionProvider = instructionProvider;
    }

    public async Task<AgentManagementPage> ExecuteAsync(Guid? id)
    {
        var agents = await _db.Agents
            .AsNoTracking()
            .Include(x => x.AiModel)
            .Include(x => x.AgentTools)
            .ThenInclude(x => x.ToolDefinition)
            .OrderBy(x => x.Name)
            .ToListAsync();

        var models = await _db.AiModels.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync();
        var tools = await _db.ToolDefinitions.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.DisplayName).ToListAsync();

        var selected = id.HasValue ? agents.FirstOrDefault(x => x.Id == id) : agents.FirstOrDefault();

        // Instruction resolution is a Service call; keep it here so the controller need not depend
        // on Services directly (see ARCHITECTURE §3).
        var instruction = selected != null ? _instructionProvider.GetInstruction(selected) : string.Empty;
        var instructionFile = selected != null
            ? $"Prompts/{AgentInstructionProvider.RelativePath(selected.RoleKey)}"
            : string.Empty;

        return new AgentManagementPage(agents, selected, models, tools, instruction, instructionFile);
    }
}
