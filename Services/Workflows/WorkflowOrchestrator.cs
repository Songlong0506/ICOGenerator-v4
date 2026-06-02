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
        var techLead = await FindActiveAgentAsync(AgentRoleKey.TechLead);
        var developer = techLead == null ? await FindActiveAgentAsync(AgentRoleKey.Developer) : null;

        var firstTaskType = techLead == null ? AgentTaskType.Implementation : AgentTaskType.ArchitectureDesign;
        var firstStage = techLead == null ? WorkflowStageKey.Implementation : WorkflowStageKey.ArchitectureDesign;
        var firstAgentId = techLead?.Id ?? developer?.Id;
        var firstTaskTitle = techLead == null
            ? "Generate POC from approved AI Design Spec"
            : "Create technical implementation plan from approved AI Design Spec";

        var workflowRun = new WorkflowRun
        {
            ProjectId = projectId,
            Name = $"Delivery Workflow {requirementVersionName}",
            Status = WorkflowRunStatus.Queued,
            CurrentStage = firstStage,
            StartedAt = null
        };

        var firstTask = new AgentTask
        {
            WorkflowRunId = workflowRun.Id,
            ProjectId = projectId,
            AgentId = firstAgentId,
            Type = firstTaskType,
            Status = AgentTaskStatus.Queued,
            Title = firstTaskTitle,
            Input = aiDesignSpecContent
        };

        _db.WorkflowRuns.Add(workflowRun);
        _db.AgentTasks.Add(firstTask);
        await _db.SaveChangesAsync();

        return workflowRun.Id;
    }

    private async Task<Agent?> FindActiveAgentAsync(AgentRoleKey roleKey)
        => await _db.Agents
            .AsNoTracking()
            .Where(agent => agent.RoleKey == roleKey && agent.Status == AgentStatus.Active)
            .OrderBy(agent => agent.CreatedAt)
            .FirstOrDefaultAsync();
}
