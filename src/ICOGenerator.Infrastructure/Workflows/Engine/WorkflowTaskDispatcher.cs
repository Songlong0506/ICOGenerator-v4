using ICOGenerator.Domain;
using ICOGenerator.Services.Workflows.Steps;

namespace ICOGenerator.Services.Workflows.Engine;

public class WorkflowTaskDispatcher
{
    private readonly IReadOnlyCollection<IWorkflowStepHandler> _handlers;

    public WorkflowTaskDispatcher(IEnumerable<IWorkflowStepHandler> handlers)
    {
        _handlers = handlers.ToArray();
    }

    public Task<WorkflowStepResult> DispatchAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var handler = _handlers.FirstOrDefault(x => x.TaskType == task.Type);
        if (handler == null)
            return Task.FromResult(WorkflowStepResult.Failed($"No workflow step handler is registered for task type '{task.Type}'."));

        return handler.ExecuteAsync(task, cancellationToken);
    }
}
