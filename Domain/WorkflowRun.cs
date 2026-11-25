using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

public class WorkflowRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;

    /// <summary>Loại run — cột để code rẽ nhánh. Xem <see cref="WorkflowRunKind"/>.</summary>
    public WorkflowRunKind Kind { get; set; } = WorkflowRunKind.Delivery;

    /// <summary>Nhãn cho người đọc ở panel tiến độ ("Delivery Workflow V1"). KHÔNG dùng để rẽ nhánh — dùng <see cref="Kind"/>.</summary>
    public string Name { get; set; } = "Delivery Workflow";

    public WorkflowRunStatus Status { get; set; } = WorkflowRunStatus.Queued;
    public WorkflowStageKey CurrentStage { get; set; } = WorkflowStageKey.RequirementApproved;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public ICollection<AgentTask> AgentTasks { get; set; } = new List<AgentTask>();
}
