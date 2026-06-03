using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows.Engine;
using ICOGenerator.Services.Workflows.Steps;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

public class WorkflowTaskDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_UsesHandlerMatchingTaskType()
    {
        var task = new AgentTask { Type = AgentTaskType.Implementation };
        var dispatcher = new WorkflowTaskDispatcher(new IWorkflowStepHandler[]
        {
            new FakeHandler(AgentTaskType.Testing, WorkflowStepResult.Failed("wrong handler")),
            new FakeHandler(AgentTaskType.Implementation, WorkflowStepResult.Completed("implemented"))
        });

        var result = await dispatcher.DispatchAsync(task, CancellationToken.None);

        Assert.Equal(AgentTaskStatus.Completed, result.TaskStatus);
        Assert.Equal(WorkflowRunStatus.Completed, result.WorkflowStatus);
        Assert.Equal("implemented", result.Output);
    }

    [Fact]
    public async Task DispatchAsync_ReturnsFailedResultWhenNoHandlerMatchesTaskType()
    {
        var task = new AgentTask { Type = AgentTaskType.CodeReview };
        var dispatcher = new WorkflowTaskDispatcher(Array.Empty<IWorkflowStepHandler>());

        var result = await dispatcher.DispatchAsync(task, CancellationToken.None);

        Assert.Equal(AgentTaskStatus.Failed, result.TaskStatus);
        Assert.Equal(WorkflowRunStatus.Failed, result.WorkflowStatus);
        Assert.Contains(nameof(AgentTaskType.CodeReview), result.Error);
    }

    private sealed class FakeHandler : IWorkflowStepHandler
    {
        private readonly WorkflowStepResult _result;

        public FakeHandler(AgentTaskType taskType, WorkflowStepResult result)
        {
            TaskType = taskType;
            _result = result;
        }

        public AgentTaskType TaskType { get; }

        public Task<WorkflowStepResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }
}
