namespace ICOGenerator.Domain.Enums;

public enum WorkflowRunStatus
{
    Queued = 1,
    Running = 2,
    WaitingForHuman = 3,
    Completed = 4,
    Failed = 5,
    Canceled = 6
}
