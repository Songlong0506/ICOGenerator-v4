using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Workflows.Steps;

public sealed record WorkflowStepResult(
    AgentTaskStatus TaskStatus,
    WorkflowRunStatus WorkflowStatus,
    WorkflowStageKey WorkflowStage,
    string? Output = null,
    string? Error = null,
    DateTime? FinishedAt = null)
{
    public static WorkflowStepResult Completed(string output, WorkflowStageKey workflowStage = WorkflowStageKey.Completed) =>
        new(
            AgentTaskStatus.Completed,
            WorkflowRunStatus.Completed,
            workflowStage,
            Output: output,
            FinishedAt: DateTime.UtcNow);

    public static WorkflowStepResult Failed(string error) =>
        new(
            AgentTaskStatus.Failed,
            WorkflowRunStatus.Failed,
            WorkflowStageKey.Failed,
            Error: error,
            FinishedAt: DateTime.UtcNow);
}
