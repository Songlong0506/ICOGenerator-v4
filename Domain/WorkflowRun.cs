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
    public ICollection<AgentTask> AgentTasks { get; set; } = new List<AgentTask>();
}
