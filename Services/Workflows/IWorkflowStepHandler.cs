namespace ICOGenerator.Services.Workflows;

public interface IWorkflowStepHandler
{
    bool CanHandle(WorkflowExecutionContext context);
    Task<WorkflowStepResult> ExecuteAsync(WorkflowExecutionContext context, CancellationToken cancellationToken);
}
