using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Workflows;

public sealed class WorkflowStepResult
{
    public string? Output { get; init; }
    public AgentTask? NextTask { get; init; }
    public WorkflowStageKey? NextStage { get; init; }
    public bool CompleteWorkflow { get; init; }
    public bool FailWorkflow { get; init; }
    public string? Error { get; init; }

    public static WorkflowStepResult Continue(string output, WorkflowStageKey nextStage, AgentTask nextTask)
        => new() { Output = output, NextStage = nextStage, NextTask = nextTask };

    public static WorkflowStepResult Complete(string output)
        => new() { Output = output, CompleteWorkflow = true, NextStage = WorkflowStageKey.Completed };

    public static WorkflowStepResult Fail(string error, string? output = null)
        => new() { Error = error, Output = output, FailWorkflow = true, NextStage = WorkflowStageKey.Failed };
}
