using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public class GetAgentManagementPageQuery
{
    private readonly AppDbContext _db;
    public GetAgentManagementPageQuery(AppDbContext db) => _db = db;

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

        return new AgentManagementPage(
            agents,
            id.HasValue ? agents.FirstOrDefault(x => x.Id == id) : agents.FirstOrDefault(),
            models,
            tools);
    }
}
