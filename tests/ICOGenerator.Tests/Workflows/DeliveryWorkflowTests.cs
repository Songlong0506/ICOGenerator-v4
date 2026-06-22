using ICOGenerator.Services.Workflows;
using ICOGenerator.Services.Workflows.Maf;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

// Structural checks for the MAF delivery-pipeline engine that don't need a live model:
// the graph builds/validates, and the Testing↔BugFix routing matches the legacy cycle.
public class DeliveryWorkflowTests
{
    private sealed class FakeStageRunner : IPipelineStageRunner
    {
        public Task<string> RunStageAsync(Guid workflowRunId, Guid projectId, PipelineStep step, string input, CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);
    }

    [Fact]
    public void Build_ConstructsAValidWorkflowGraph()
    {
        var factory = new DeliveryWorkflowFactory(new FakeStageRunner());

        // Build wires every stage, the four approval gates and the Testing↔BugFix loop, then validates the
        // graph — a throw here would mean the edges/ports are misconfigured.
        var workflow = factory.Build(Guid.NewGuid(), Guid.NewGuid());

        Assert.NotNull(workflow);
    }

    [Theory]
    [InlineData("All suites green. VERDICT: PASS", 0, false)] // pass → finish
    [InlineData("Found a bug. VERDICT: FAIL", 0, true)]       // fail, budget left → loop
    [InlineData("Found a bug. VERDICT: FAIL", 2, true)]       // fail, attempt 2 (< 3) → loop
    [InlineData("Found a bug. VERDICT: FAIL", 3, false)]      // fail, budget exhausted → stop
    [InlineData("report without a verdict line", 0, false)]   // unknown → treated as pass
    public void ShouldAttemptBugFix_MatchesLegacyCycle(string testOutput, int bugFixAttempt, bool expected)
        => Assert.Equal(expected, DeliveryWorkflowRouting.ShouldAttemptBugFix(testOutput, bugFixAttempt));
}
