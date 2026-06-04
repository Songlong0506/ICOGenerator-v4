namespace ICOGenerator.Services.Workflows;

public sealed class WorkflowStepDispatcher
{
    private readonly IEnumerable<IWorkflowStepHandler> _handlers;

    public WorkflowStepDispatcher(IEnumerable<IWorkflowStepHandler> handlers)
    {
        _handlers = handlers;
    }

    public IWorkflowStepHandler? Resolve(WorkflowExecutionContext context)
        => _handlers.FirstOrDefault(handler => handler.CanHandle(context));
}
