using ICOGenerator.Domain;

namespace ICOGenerator.Services.Workflows;

public sealed class WorkflowExecutionContext
{
    public WorkflowRun WorkflowRun { get; init; } = default!;
    public AgentTask CurrentTask { get; init; } = default!;
    public Project Project { get; init; } = default!;
    public IReadOnlyList<AgentTask> PreviousTasks { get; init; } = Array.Empty<AgentTask>();
}
