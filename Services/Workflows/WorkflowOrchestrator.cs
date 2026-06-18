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
        // Start at the first declared pipeline step (Tech Lead) instead of hard-coding the developer;
        // the worker drives the hand-off from here based on DeliveryPipeline.
        var first = DeliveryPipeline.First;
        var agent = await _db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RoleKey == first.Role);

        var workflowRun = new WorkflowRun
        {
            ProjectId = projectId,
            Name = $"Delivery Workflow {requirementVersionName}",
            Status = WorkflowRunStatus.Queued,
            CurrentStage = first.Stage,
            StartedAt = null
        };

        var firstTask = new AgentTask
        {
            WorkflowRunId = workflowRun.Id,
            ProjectId = projectId,
            AgentId = agent?.Id,
            Type = first.TaskType,
            Status = AgentTaskStatus.Queued,
            Title = first.Title,
            // The AI Design Spec rides along as Input through every stage (the worker carries it
            // forward unchanged), so the developer's POC step receives exactly what it does today
            // while other roles read further artifacts (architecture, POC) from the workspace via tools.
            Input = aiDesignSpecContent
        };

        _db.WorkflowRuns.Add(workflowRun);
        _db.AgentTasks.Add(firstTask);
        await _db.SaveChangesAsync();

        return workflowRun.Id;
    }

    public async Task<Guid> StartRequirementDraftWorkflowAsync(Guid projectId)
    {
        var ba = await _db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst);

        var workflowRun = new WorkflowRun
        {
            ProjectId = projectId,
            Name = "Write Requirement",
            Status = WorkflowRunStatus.Queued,
            CurrentStage = WorkflowStageKey.RequirementApproved,
            StartedAt = null
        };

        var draftTask = new AgentTask
        {
            WorkflowRunId = workflowRun.Id,
            ProjectId = projectId,
            AgentId = ba?.Id,
            Type = AgentTaskType.RequirementAnalysis,
            Status = AgentTaskStatus.Queued,
            Title = "Generate/update requirement documents from conversation"
        };

        _db.WorkflowRuns.Add(workflowRun);
        _db.AgentTasks.Add(draftTask);
        await _db.SaveChangesAsync();

        return workflowRun.Id;
    }
}
