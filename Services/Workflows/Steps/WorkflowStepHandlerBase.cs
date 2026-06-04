using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Workflows.Steps;

public abstract class WorkflowStepHandlerBase : IWorkflowStepHandler
{
    protected WorkflowStepHandlerBase(AppDbContext db, AgentRunService agentRunService)
    {
        Db = db;
        AgentRunService = agentRunService;
    }

    protected AppDbContext Db { get; }
    protected AgentRunService AgentRunService { get; }

    public abstract bool CanHandle(WorkflowExecutionContext context);
    public abstract Task<WorkflowStepResult> ExecuteAsync(WorkflowExecutionContext context, CancellationToken cancellationToken);

    protected async Task<Agent?> FindActiveAgentAsync(AgentRoleKey roleKey, CancellationToken cancellationToken)
        => await Db.Agents
            .AsNoTracking()
            .Where(agent => agent.RoleKey == roleKey && agent.Status == AgentStatus.Active)
            .OrderBy(agent => agent.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    protected static AgentTask CreateTask(
        WorkflowExecutionContext context,
        Guid? agentId,
        AgentTaskType type,
        string title,
        string input)
        => new()
        {
            WorkflowRunId = context.WorkflowRun.Id,
            ProjectId = context.Project.Id,
            AgentId = agentId,
            Type = type,
            Status = AgentTaskStatus.Queued,
            Title = title,
            Input = input
        };

    protected static string BuildInputWithPreviousOutput(string baseInput, string previousSectionTitle, string previousOutput)
        => $"""
{baseInput}

# {previousSectionTitle}

{previousOutput}
""";
}
