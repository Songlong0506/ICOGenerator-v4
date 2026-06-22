using ICOGenerator.Domain.Enums;
using Microsoft.Agents.AI.Workflows;

namespace ICOGenerator.Services.Workflows.Maf;

/// <summary>
/// Builds the Microsoft Agent Framework <see cref="Workflow"/> for the delivery pipeline — the engine's
/// declarative graph, replacing the imperative hand-off logic in the legacy worker:
///
///   POC ─gate─ Architecture ─gate─ Implementation ─gate─ CodeReview ─gate─ Testing ─► (output)
///                                                                          │  ▲
///                                                                          ▼  │
///                                                                        BugFix
///
/// Each linear hand-off goes through a human-approval gate (a <see cref="RequestPort"/> that halts the
/// run until Approve/Reject). Testing↔BugFix is an automatic loop (no gate), bounded by
/// <see cref="DeliveryPipeline.MaxBugFixAttempts"/> via the decision inside the Testing executor.
///
/// A fresh workflow is built per run (executors capture the run/project ids); the engine driver runs it
/// with the EF checkpoint store so it is durable and resumable.
/// </summary>
public sealed class DeliveryWorkflowFactory
{
    /// <summary>RequestPort id prefix for an approval gate; the suffix is the stage just completed.</summary>
    public const string ApprovalGatePrefix = "approval-gate:";

    private readonly IPipelineStageRunner _runner;

    public DeliveryWorkflowFactory(IPipelineStageRunner runner) => _runner = runner;

    /// <summary>The approval gate id placed after <paramref name="completedStage"/>.</summary>
    public static string GateId(WorkflowStageKey completedStage) => ApprovalGatePrefix + completedStage;

    public Workflow Build(Guid workflowRunId, Guid projectId)
    {
        var steps = DeliveryPipeline.Steps;

        PipelineStageExecutor Stage(Domain.Enums.WorkflowStageKey stage) =>
            new(stage.ToString(), DeliveryPipeline.Find(stage)!, workflowRunId, projectId, _runner);

        var executors = steps.ToDictionary(s => s.Stage, s => Stage(s.Stage));
        var bugFix = Stage(WorkflowStageKey.BugFix);

        var builder = new WorkflowBuilder(executors[steps[0].Stage].BindExecutor());

        // Linear stages separated by human-approval gates (request ports passing the message through).
        for (var i = 0; i < steps.Count - 1; i++)
        {
            var from = executors[steps[i].Stage].BindExecutor();
            var to = executors[steps[i + 1].Stage].BindExecutor();
            var gate = RequestPort.Create<PipelineMessage, PipelineMessage>(GateId(steps[i].Stage)).BindAsExecutor();
            builder.AddEdge(from, gate);
            builder.AddEdge(gate, to);
        }

        // Automatic Testing↔BugFix loop (no gate); Testing yields the final output when it stops looping.
        var testing = executors[WorkflowStageKey.Testing].BindExecutor();
        builder.AddEdge(testing, bugFix.BindExecutor());
        builder.AddEdge(bugFix.BindExecutor(), testing);
        builder.WithOutputFrom(testing);

        return builder.Build(validateOrphans: false);
    }
}
