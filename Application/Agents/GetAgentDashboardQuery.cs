using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

public record AgentDashboardResult(Project Project, IReadOnlyList<Agent> Agents, IReadOnlyList<string> Phases);

public class GetAgentDashboardQuery
{
    private readonly AppDbContext _db;

    public GetAgentDashboardQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AgentDashboardResult?> ExecuteAsync(Guid projectId)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId);
        if (project == null)
            return null;

        project.Documents = await _db.ProjectDocuments.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Folder)
            .ThenBy(x => x.FileName)
            .ToListAsync();

        project.Conversations = await _db.AgentConversations.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .Include(x => x.Agent)
            .OrderByDescending(x => x.CreatedAt)
            .Take(50)
            .ToListAsync();

        project.ModelCallLogs = await _db.AgentModelCallLogs.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .Select(x => new AgentModelCallLog
            {
                Id = x.Id,
                ModelName = x.ModelName,
                TotalTokens = x.TotalTokens,
                DurationMs = x.DurationMs,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync();

        var agents = await _db.Agents.AsNoTracking()
            .Include(x => x.AgentTools)
            .ThenInclude(x => x.ToolDefinition)
            .ToListAsync();

        return new AgentDashboardResult(project, agents, ProjectWorkspaceLayout.Phases);
    }
}
