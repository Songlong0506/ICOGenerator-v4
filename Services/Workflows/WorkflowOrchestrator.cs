using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Workflows;

public class WorkflowOrchestrator : IWorkflowOrchestrator
{
    private readonly AppDbContext _db;

    public WorkflowOrchestrator(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> StartDeliveryWorkflowAsync(Guid projectId, string requirementVersionName, string aiDesignSpecContent)
    {
        var developer = await _db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.Developer);

        var workflowRun = new WorkflowRun
        {
            ProjectId = projectId,
            Name = $"Delivery Workflow {requirementVersionName}",
            Status = WorkflowRunStatus.Queued,
            CurrentStage = WorkflowStageKey.Implementation,
            StartedAt = null
        };

        var implementationTask = new AgentTask
        {
            WorkflowRunId = workflowRun.Id,
            ProjectId = projectId,
            AgentId = developer?.Id,
            Type = AgentTaskType.Implementation,
            Status = AgentTaskStatus.Queued,
            Title = "Generate POC from approved AI Design Spec",
            Input = aiDesignSpecContent
        };

        _db.WorkflowRuns.Add(workflowRun);
        _db.AgentTasks.Add(implementationTask);
        await _db.SaveChangesAsync();

        return workflowRun.Id;
    }
}
