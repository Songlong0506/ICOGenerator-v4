using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public class UpdateAgentUseCase
{
    private readonly AppDbContext _db;
    public UpdateAgentUseCase(AppDbContext db) => _db = db;

    public async Task<bool> ExecuteAsync(AgentEditVm vm)
    {
        var agent = await _db.Agents.Include(x => x.AgentTools).FirstOrDefaultAsync(x => x.Id == vm.Id);
        if (agent == null)
            return false;

        agent.Name = vm.Name?.Trim() ?? string.Empty;
        agent.RoleTitle = vm.RoleTitle?.Trim() ?? string.Empty;
        agent.Description = vm.Description?.Trim() ?? string.Empty;
        agent.Instruction = vm.Instruction ?? string.Empty;
        agent.Color = string.IsNullOrWhiteSpace(vm.Color) ? "#8B5CF6" : vm.Color.Trim();
        agent.Status = vm.Status;
        agent.Temperature = vm.Temperature;
        agent.AiModelId = vm.AiModelId;

        var selectedToolIds = vm.ToolDefinitionIds.Distinct().ToHashSet();
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
