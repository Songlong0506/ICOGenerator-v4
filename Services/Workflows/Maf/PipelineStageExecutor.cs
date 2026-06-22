using ICOGenerator.Domain.Enums;
using Microsoft.Agents.AI.Workflows;

namespace ICOGenerator.Services.Workflows.Maf;

/// <summary>
/// A MAF executor for one delivery-pipeline stage. It runs the stage via <see cref="IPipelineStageRunner"/>
/// then routes the result:
///   • Testing → loops to BugFix (send message) while the bug-fix budget allows, otherwise yields the
///     workflow output;
///   • BugFix → sends back to Testing with the attempt counter incremented;
///   • any linear stage → sends downstream (the edge leads to a human-approval gate, then the next stage).
/// The decision lives in the executor (not in edge predicates) so it can read both the test verdict and
/// the attempt count.
/// </summary>
public sealed class PipelineStageExecutor : Executor<PipelineMessage>
{
    private readonly PipelineStep _step;
    private readonly Guid _workflowRunId;
    private readonly Guid _projectId;
    private readonly IPipelineStageRunner _runner;

    public PipelineStageExecutor(string id, PipelineStep step, Guid workflowRunId, Guid projectId, IPipelineStageRunner runner)
        : base(id)
    {
        _step = step;
        _workflowRunId = workflowRunId;
        _projectId = projectId;
        _runner = runner;
    }

    public override async ValueTask HandleAsync(PipelineMessage message, IWorkflowContext context, CancellationToken cancellationToken)
    {
        // POC/Architecture consume the design spec; later stages consume the previous stage's output.
        var input = _step.InputSource == PipelineInputSource.DesignSpec ? message.DesignSpec : message.Content;
        var output = await _runner.RunStageAsync(_workflowRunId, _projectId, _step, input, cancellationToken);

        if (_step.Stage == WorkflowStageKey.Testing)
        {
            if (DeliveryWorkflowRouting.ShouldAttemptBugFix(output, message.BugFixAttempt))
                await context.SendMessageAsync(message with { Content = output }, cancellationToken: cancellationToken);
            else
                await context.YieldOutputAsync(message with { Content = output }, cancellationToken);
            return;
        }

        if (_step.Stage == WorkflowStageKey.BugFix)
        {
            await context.SendMessageAsync(message with { Content = output, BugFixAttempt = message.BugFixAttempt + 1 }, cancellationToken: cancellationToken);
            return;
        }

        // Linear stage: hand off downstream (next edge is an approval gate).
        await context.SendMessageAsync(message with { Content = output }, cancellationToken: cancellationToken);
    }
}
