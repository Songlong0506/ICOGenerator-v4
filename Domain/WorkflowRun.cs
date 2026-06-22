using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

public class WorkflowRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;
    public string Name { get; set; } = "Default Delivery Workflow";
    public WorkflowRunStatus Status { get; set; } = WorkflowRunStatus.Queued;
    public WorkflowStageKey CurrentStage { get; set; } = WorkflowStageKey.RequirementApproved;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>
    /// Only used by the opt-in MAF workflow engine: when a run halts at a human-approval gate
    /// (<see cref="Domain.Enums.WorkflowRunStatus.WaitingForHuman"/>), the pending MAF external request
    /// is serialized here so Approve/Reject can resume the checkpointed workflow even after an app
    /// restart. Null under the default DB-task worker.
    /// </summary>
    public string? PendingApprovalJson { get; set; }

    public ICollection<AgentTask> AgentTasks { get; set; } = new List<AgentTask>();
}
