using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Workflows;

public sealed class WorkflowStateApplier
{
    private readonly AppDbContext _db;

    public WorkflowStateApplier(AppDbContext db)
    {
        _db = db;
    }

    public async Task ApplyAsync(WorkflowExecutionContext context, WorkflowStepResult result, CancellationToken cancellationToken)
    {
        var task = context.CurrentTask;
        var workflowRun = context.WorkflowRun;

        task.Output = result.Output;
        task.FinishedAt = DateTime.UtcNow;

        if (result.FailWorkflow)
        {
            task.Status = AgentTaskStatus.Failed;
            task.Error = result.Error;
            workflowRun.Status = WorkflowRunStatus.Failed;
            workflowRun.CurrentStage = WorkflowStageKey.Failed;
            workflowRun.FinishedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        task.Status = AgentTaskStatus.Completed;

        if (result.CompleteWorkflow)
        {
            workflowRun.Status = WorkflowRunStatus.Completed;
            workflowRun.CurrentStage = WorkflowStageKey.Completed;
            workflowRun.FinishedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (result.NextTask == null || result.NextStage == null)
        {
            task.Status = AgentTaskStatus.Failed;
            task.Error = "Workflow step did not return a next task or completion result.";
            workflowRun.Status = WorkflowRunStatus.Failed;
            workflowRun.CurrentStage = WorkflowStageKey.Failed;
            workflowRun.FinishedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        workflowRun.Status = WorkflowRunStatus.Running;
        workflowRun.CurrentStage = result.NextStage.Value;
        _db.AgentTasks.Add(result.NextTask);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task FailAsync(AgentTask task, string error, CancellationToken cancellationToken)
    {
        task.Status = AgentTaskStatus.Failed;
        task.Error = error;
        task.FinishedAt = DateTime.UtcNow;
        task.WorkflowRun.Status = WorkflowRunStatus.Failed;
        task.WorkflowRun.CurrentStage = WorkflowStageKey.Failed;
        task.WorkflowRun.FinishedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
