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
        // Bắt đầu pipeline ở bước đầu tiên (khai báo trong DeliveryPipeline) thay vì
        // cứng nhắc giao thẳng cho Developer. Các bước sau do AgentTaskWorker hand-off.
        var first = DeliveryPipeline.First;

        var firstAgent = await _db.Agents
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
            AgentId = firstAgent?.Id,
            Type = first.TaskType,
            Status = AgentTaskStatus.Queued,
            Title = first.Title,
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

    public async Task<Guid> StartTechnicalDocsWorkflowAsync(Guid projectId)
    {
        var ba = await _db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst);

        var workflowRun = new WorkflowRun
        {
            ProjectId = projectId,
            Name = "Generate Technical Docs",
            Status = WorkflowRunStatus.Queued,
            CurrentStage = WorkflowStageKey.RequirementApproved,
            StartedAt = null
        };

        var docsTask = new AgentTask
        {
            WorkflowRunId = workflowRun.Id,
            ProjectId = projectId,
            AgentId = ba?.Id,
            Type = AgentTaskType.TechnicalDocs,
            Status = AgentTaskStatus.Queued,
            Title = "Generate technical documents (BRD/SRS/FSD/UserStories)"
        };

        _db.WorkflowRuns.Add(workflowRun);
        _db.AgentTasks.Add(docsTask);
        await _db.SaveChangesAsync();

        return workflowRun.Id;
    }
}
