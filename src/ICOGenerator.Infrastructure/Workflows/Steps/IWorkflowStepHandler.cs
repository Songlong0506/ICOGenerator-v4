using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Workflows.Steps;

public interface IWorkflowStepHandler
{
    AgentTaskType TaskType { get; }

    Task<WorkflowStepResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken);
}
