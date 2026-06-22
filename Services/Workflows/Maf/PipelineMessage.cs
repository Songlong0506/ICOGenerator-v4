using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Workflows;

namespace ICOGenerator.Services.Workflows.Maf;

/// <summary>
/// The message that flows along the MAF delivery-pipeline graph. It is also the type checkpointed
/// mid-flight, so it stays a small JSON-serializable record.
///
/// <para><see cref="Content"/> is the running hand-off payload (each stage's output becomes the next
/// stage's input). <see cref="DesignSpec"/> is the original approved AI Design Spec, carried for the
/// whole run because the POC and Architecture stages take the spec as input rather than the previous
/// stage's output (see <see cref="PipelineStep.InputSource"/>) — carrying it on the message avoids
/// needing shared workflow state. <see cref="BugFixAttempt"/> counts completed BugFix rounds, bounding
/// the automatic Testing↔BugFix loop.</para>
/// </summary>
public sealed record PipelineMessage(string Content, string DesignSpec, int BugFixAttempt = 0);

/// <summary>
/// Pure routing decisions for the delivery workflow, kept separate from the executors so they can be
/// unit-tested without spinning up the workflow engine.
/// </summary>
public static class DeliveryWorkflowRouting
{
    /// <summary>
    /// After a Testing stage produces <paramref name="testOutput"/>, should the workflow loop back into a
    /// BugFix round? True only when the tester reported FAIL and the bug-fix budget isn't exhausted —
    /// matching the legacy worker's Testing↔BugFix cycle (<see cref="DeliveryPipeline.MaxBugFixAttempts"/>).
    /// </summary>
    public static bool ShouldAttemptBugFix(string testOutput, int bugFixAttempt) =>
        TestVerdictParser.Parse(testOutput) == TestVerdict.Fail
        && bugFixAttempt < DeliveryPipeline.MaxBugFixAttempts;
}
