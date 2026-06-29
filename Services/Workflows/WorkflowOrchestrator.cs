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

    public async Task<Guid> StartAiDesignSpecWorkflowAsync(Guid projectId, string requirementVersionName)
    {
        var ba = await _db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RoleKey == AgentRoleKey.BusinessAnalyst);

        // Tên chứa "Requirement" để UI hiển thị panel "Requirement Progress" (run một bước của BA, không
        // phải pipeline delivery). versionName được nhét vào Input để worker biết phiên bản nào cần sinh spec.
        var workflowRun = new WorkflowRun
        {
            ProjectId = projectId,
            Name = $"Requirement Design Spec {requirementVersionName}",
            Status = WorkflowRunStatus.Queued,
            CurrentStage = WorkflowStageKey.RequirementApproved,
            StartedAt = null
        };

        var specTask = new AgentTask
        {
            WorkflowRunId = workflowRun.Id,
            ProjectId = projectId,
            AgentId = ba?.Id,
            Type = AgentTaskType.AiDesignSpec,
            Status = AgentTaskStatus.Queued,
            Title = "Sinh AI Design Spec từ Product Brief đã duyệt",
            Input = requirementVersionName
        };

        _db.WorkflowRuns.Add(workflowRun);
        _db.AgentTasks.Add(specTask);
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
