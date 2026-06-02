using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Project> Projects { get; }
    DbSet<Agent> Agents { get; }
    DbSet<AiModel> AiModels { get; }
    DbSet<ToolDefinition> ToolDefinitions { get; }
    DbSet<AgentTool> AgentTools { get; }
    DbSet<ProjectDocument> ProjectDocuments { get; }
    DbSet<AgentConversation> AgentConversations { get; }
    DbSet<AgentJob> AgentJobs { get; }
    DbSet<AgentModelCallLog> AgentModelCallLogs { get; }
    DbSet<WorkflowRun> WorkflowRuns { get; }
    DbSet<AgentTask> AgentTasks { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
