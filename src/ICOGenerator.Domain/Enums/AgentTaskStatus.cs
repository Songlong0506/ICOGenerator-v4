namespace ICOGenerator.Domain.Enums;

public enum AgentTaskStatus
{
    Queued = 1,
    Running = 2,
    Blocked = 3,
    NeedsReview = 4,
    Completed = 5,
    Failed = 6,
    Retrying = 7,
    Canceled = 8
}
