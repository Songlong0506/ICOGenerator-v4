using ICOGenerator.Services.Workflows;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

public class WorkflowStepDispatcherTests
{
    [Fact]
    public void Resolve_ReturnsFirstHandlerThatCanHandleContext()
    {
        var first = new StubWorkflowStepHandler(false);
        var second = new StubWorkflowStepHandler(true);
        var dispatcher = new WorkflowStepDispatcher(new IWorkflowStepHandler[] { first, second });

        var resolved = dispatcher.Resolve(new WorkflowExecutionContext());

        Assert.Same(second, resolved);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoHandlerCanHandleContext()
    {
        var dispatcher = new WorkflowStepDispatcher(new IWorkflowStepHandler[]
        {
            new StubWorkflowStepHandler(false)
        });

        var resolved = dispatcher.Resolve(new WorkflowExecutionContext());

        Assert.Null(resolved);
    }

    private sealed class StubWorkflowStepHandler : IWorkflowStepHandler
    {
        private readonly bool _canHandle;

        public StubWorkflowStepHandler(bool canHandle)
        {
            _canHandle = canHandle;
        }

        public bool CanHandle(WorkflowExecutionContext context) => _canHandle;

        public Task<WorkflowStepResult> ExecuteAsync(WorkflowExecutionContext context, CancellationToken cancellationToken)
            => Task.FromResult(WorkflowStepResult.Complete("Done"));
    }
}
