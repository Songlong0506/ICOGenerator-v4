using ICOGenerator.Application.Abstractions;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public record AgentManagementPage(IReadOnlyList<Agent> Agents, Agent? SelectedAgent, IReadOnlyList<AiModel> Models, IReadOnlyList<ToolDefinition> Tools);

public class GetAgentManagementPageQuery
{
    private readonly IAppDbContext _db;
    public GetAgentManagementPageQuery(IAppDbContext db) => _db = db;

    public async Task<AgentManagementPage> ExecuteAsync(Guid? id)
    {
        var agents = await _db.Agents
            .Include(x => x.AiModel)
            .Include(x => x.AgentTools)
            .ThenInclude(x => x.ToolDefinition)
            .OrderBy(x => x.Name)
            .ToListAsync();

        var models = await _db.AiModels.Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync();
        var tools = await _db.ToolDefinitions.Where(x => x.IsActive).OrderBy(x => x.DisplayName).ToListAsync();

        return new AgentManagementPage(
            agents,
            id.HasValue ? agents.FirstOrDefault(x => x.Id == id) : agents.FirstOrDefault(),
            models,
            tools);
    }
}

public class UpdateAgentUseCase
{
    private readonly IAppDbContext _db;
    public UpdateAgentUseCase(IAppDbContext db) => _db = db;

    public async Task<bool> ExecuteAsync(UpdateAgentCommand command)
    {
        var agent = await _db.Agents.Include(x => x.AgentTools).FirstOrDefaultAsync(x => x.Id == command.Id);
        if (agent == null)
            return false;

        agent.Name = command.Name?.Trim() ?? string.Empty;
        agent.RoleTitle = command.RoleTitle?.Trim() ?? string.Empty;
        agent.Description = command.Description?.Trim() ?? string.Empty;
        agent.Instruction = command.Instruction ?? string.Empty;
        agent.Color = string.IsNullOrWhiteSpace(command.Color) ? "#8B5CF6" : command.Color.Trim();
        agent.Status = command.Status;
        agent.Temperature = command.Temperature;
        agent.AiModelId = command.AiModelId;

        var selectedToolIds = command.ToolDefinitionIds.Distinct().ToHashSet();
        var removed = agent.AgentTools.Where(x => !selectedToolIds.Contains(x.ToolDefinitionId)).ToList();
        _db.AgentTools.RemoveRange(removed);

        var existingToolIds = agent.AgentTools.Select(x => x.ToolDefinitionId).ToHashSet();
        var newToolIds = selectedToolIds.Where(id => !existingToolIds.Contains(id)).ToList();
        foreach (var toolId in newToolIds)
        {
            var toolExists = await _db.ToolDefinitions.AnyAsync(x => x.Id == toolId && x.IsActive);
            if (!toolExists)
                continue;

            _db.AgentTools.Add(new AgentTool { AgentId = agent.Id, ToolDefinitionId = toolId });
        }

        await _db.SaveChangesAsync();
        return true;
    }
}
