using System.Collections.Concurrent;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;
using ICOGenerator.Services.Workflows.Maf;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

// Drives the REAL MAF delivery workflow (built by DeliveryWorkflowFactory) end to end with a fake stage
// runner — no live model. It exercises the parts that can't be reasoned about statically: the four
// human-approval gates (suspend → resume-from-checkpoint → answer), and the automatic Testing↔BugFix
// loop. This is the integration proof that the engine wiring actually works.
public class DeliveryWorkflowExecutionTests
{
    // Returns canned per-stage output; Testing fails once (to force one BugFix round) then passes.
    private sealed class FakeStageRunner : IPipelineStageRunner
    {
        public readonly ConcurrentQueue<WorkflowStageKey> Ran = new();
        private int _testingCalls;

        public Task<string> RunStageAsync(Guid workflowRunId, Guid projectId, PipelineStep step, string input, CancellationToken cancellationToken)
        {
            Ran.Enqueue(step.Stage);
            var output = step.Stage switch
            {
                WorkflowStageKey.Testing => Interlocked.Increment(ref _testingCalls) == 1
                    ? "Found a failing test. VERDICT: FAIL"
                    : "All suites green. VERDICT: PASS",
                WorkflowStageKey.BugFix => "Applied a fix.",
                _ => $"{step.Stage} output"
            };
            return Task.FromResult(output);
        }
    }

    [Fact]
    public async Task FullRun_ApprovesEveryGate_RunsBugFixLoop_AndCompletes()
    {
        var runner = new FakeStageRunner();
        var factory = new DeliveryWorkflowFactory(runner);
        var checkpoints = CheckpointManager.CreateInMemory();
        var sessionId = Guid.NewGuid().ToString();
        var runId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var seed = new PipelineMessage("the approved design spec", "the approved design spec");

        // Start the run; it advances to the first approval gate (after POC).
        var run = await InProcessExecution.RunStreamingAsync(factory.Build(runId, projectId), seed, checkpoints, sessionId);
        var (done, output, lastCheckpoint) = await PumpToGateOrDone(run);

        var gatesApproved = 0;
        while (!done)
        {
            // Approve: resume a FRESH workflow from the checkpoint (mirrors how Approve/restart works), let
            // it re-emit the pending gate request, answer it (pass the message through), then advance.
            gatesApproved++;
            run = await InProcessExecution.ResumeStreamingAsync(factory.Build(runId, projectId), lastCheckpoint!, checkpoints);

            var reEmitted = await PumpToGate(run);
            Assert.NotNull(reEmitted);
            await run.SendResponseAsync(reEmitted!.CreateResponse(reEmitted.Data.As<PipelineMessage>()));

            (done, output, lastCheckpoint) = await PumpToGateOrDone(run);
        }

        // Four gates: after POC, Architecture, Implementation, CodeReview (Testing↔BugFix has no gate).
        Assert.Equal(4, gatesApproved);

        // The final yielded output is the passing test report.
        Assert.Contains("VERDICT: PASS", output);

        // Every stage ran, and the Testing↔BugFix loop executed exactly: Testing, BugFix, Testing.
        var ran = runner.Ran.ToList();
        Assert.Equal(
            new[]
            {
                WorkflowStageKey.PocPreview,
                WorkflowStageKey.ArchitectureDesign,
                WorkflowStageKey.Implementation,
                WorkflowStageKey.CodeReview,
                WorkflowStageKey.Testing,
                WorkflowStageKey.BugFix,
                WorkflowStageKey.Testing
            },
            ran);
    }

    // Pumps until the run halts at an approval gate (returns the checkpoint to resume from) or completes.
    private static async Task<(bool done, string output, CheckpointInfo? checkpoint)> PumpToGateOrDone(StreamingRun run)
    {
        await foreach (var evt in run.WatchStreamAsync())
        {
            switch (evt)
            {
                case RequestInfoEvent:
                    return (false, string.Empty, run.LastCheckpoint);
                case WorkflowOutputEvent output:
                    return (true, output.As<PipelineMessage>()?.Content ?? string.Empty, run.LastCheckpoint);
                case ExecutorFailedEvent failed:
                    throw new Xunit.Sdk.XunitException("Executor failed: " + failed.Data);
            }
        }
        return (true, string.Empty, run.LastCheckpoint);
    }

    // Pumps a freshly-resumed run until it re-emits the pending gate request.
    private static async Task<ExternalRequest?> PumpToGate(StreamingRun run)
    {
        await foreach (var evt in run.WatchStreamAsync())
        {
            if (evt is RequestInfoEvent request)
                return request.Request;
            if (evt is ExecutorFailedEvent failed)
                throw new Xunit.Sdk.XunitException("Executor failed on resume: " + failed.Data);
        }
        return null;
    }
}
