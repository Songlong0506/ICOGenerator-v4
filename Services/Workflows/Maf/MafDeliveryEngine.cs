using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using Microsoft.Agents.AI.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Workflows.Maf;

/// <summary>
/// Drives one delivery-pipeline run on the Microsoft Agent Framework workflow engine: starts it,
/// resumes it (after approval, retry, or app restart), pumps its events, and projects the result back
/// onto the existing <see cref="Domain.WorkflowRun"/> status the UI reads. Durable via the EF checkpoint
/// store; a run halts at each human-approval gate and resumes from its checkpoint.
///
/// Intent is read from persisted state so the engine survives restarts:
///   • no checkpoints            → fresh start (seed = the approved AI Design Spec);
///   • checkpoints + "approved"  → resume and answer the pending gate (advance one stage);
///   • checkpoints, no marker    → resume without answering (retry / restart recovery).
/// </summary>
public sealed class MafDeliveryEngine
{
    /// <summary>Marker written to <see cref="Domain.WorkflowRun.PendingApprovalJson"/> by Approve to tell the driver to answer the pending gate.</summary>
    public const string ApprovedMarker = "approved";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DeliveryWorkflowFactory _workflowFactory;
    private readonly EfWorkflowCheckpointStore _checkpointStore;
    private readonly IWorkflowProgressReporter _progress;

    public MafDeliveryEngine(IServiceScopeFactory scopeFactory, DeliveryWorkflowFactory workflowFactory, EfWorkflowCheckpointStore checkpointStore, IWorkflowProgressReporter progress)
    {
        _scopeFactory = scopeFactory;
        _workflowFactory = workflowFactory;
        _checkpointStore = checkpointStore;
        _progress = progress;
    }

    /// <summary>
    /// Advances a delivery run from its current persisted state to the next gate, completion, or failure.
    /// </summary>
    public async Task DriveAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        Guid projectId;
        bool answerApprove;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.WorkflowRuns.FirstAsync(r => r.Id == workflowRunId, cancellationToken);
            projectId = run.ProjectId;
            answerApprove = string.Equals(run.PendingApprovalJson, ApprovedMarker, StringComparison.Ordinal);

            // Claim the run and clear the approval marker so a crash mid-drive can't double-approve.
            run.Status = WorkflowRunStatus.Running;
            run.StartedAt ??= DateTime.UtcNow;
            run.PendingApprovalJson = null;
            await db.SaveChangesAsync(cancellationToken);
        }

        var sessionId = workflowRunId.ToString();
        var checkpoints = CheckpointManager.CreateJson(_checkpointStore, null);
        var existing = (await _checkpointStore.RetrieveIndexAsync(sessionId)).ToList();

        await using var run2 = existing.Count == 0
            ? await InProcessExecution.RunStreamingAsync(
                _workflowFactory.Build(workflowRunId, projectId),
                await ResolveSeedAsync(projectId, cancellationToken),
                checkpoints, sessionId, cancellationToken)
            : await InProcessExecution.ResumeStreamingAsync(
                _workflowFactory.Build(workflowRunId, projectId),
                existing[^1], checkpoints, cancellationToken);

        await PumpAsync(run2, workflowRunId, existing.Count > 0 && answerApprove, cancellationToken);
    }

    private async Task PumpAsync(StreamingRun run, Guid workflowRunId, bool answerPendingGate, CancellationToken cancellationToken)
    {
        while (true)
        {
            ExternalRequest? gate = null;
            var terminal = false;

            await foreach (var evt in run.WatchStreamAsync(cancellationToken))
            {
                if (evt is RequestInfoEvent request)
                {
                    gate = request.Request;
                    break;
                }
                if (evt is WorkflowOutputEvent)
                {
                    await CompleteAsync(workflowRunId, cancellationToken);
                    terminal = true;
                    break;
                }
                if (evt is ExecutorFailedEvent failed)
                {
                    await FailAsync(workflowRunId, failed.Data?.Message ?? "Workflow executor failed.", cancellationToken);
                    terminal = true;
                    break;
                }
                if (evt is WorkflowErrorEvent error)
                {
                    await FailAsync(workflowRunId, (error.Data as Exception)?.Message ?? "Workflow error.", cancellationToken);
                    terminal = true;
                    break;
                }
            }

            if (terminal || gate is null)
                return;

            if (answerPendingGate)
            {
                // The gate we just resumed onto is the one Approve answered: pass the message through and
                // keep pumping so the next stage runs (and we stop at the following gate / completion).
                answerPendingGate = false;
                await run.SendResponseAsync(gate.CreateResponse(gate.Data.As<PipelineMessage>()));
                continue;
            }

            // A new gate: halt for human approval.
            await WaitForHumanAsync(workflowRunId, cancellationToken);
            return;
        }
    }

    private async Task<PipelineMessage> ResolveSeedAsync(Guid projectId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var catalog = scope.ServiceProvider.GetRequiredService<IProjectArtifactCatalog>();

        var spec = await db.ProjectDocuments
            .Where(d => d.ProjectId == projectId && d.IsApproved && d.FileName == catalog.AiDesignSpec.FileName)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => d.Content)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        return new PipelineMessage(spec, spec);
    }

    private async Task WaitForHumanAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        await UpdateRunAsync(workflowRunId, run =>
        {
            run.Status = WorkflowRunStatus.WaitingForHuman;
            run.PendingApprovalJson = null;
        }, cancellationToken);
        _progress.Report(workflowRunId, "completed", "Bước đã xong — chờ bạn duyệt để sang bước kế.");
    }

    private async Task CompleteAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        await UpdateRunAsync(workflowRunId, run =>
        {
            run.Status = WorkflowRunStatus.Completed;
            run.CurrentStage = WorkflowStageKey.Completed;
            run.FinishedAt = DateTime.UtcNow;
        }, cancellationToken);
        _progress.Report(workflowRunId, "completed", "Workflow hoàn tất — tất cả các bước đã xong.");
    }

    private async Task FailAsync(Guid workflowRunId, string error, CancellationToken cancellationToken)
    {
        await UpdateRunAsync(workflowRunId, run =>
        {
            run.Status = WorkflowRunStatus.Failed;
            // CurrentStage is left at the failing delivery stage (not WorkflowStageKey.Failed) so RetryWorkflow
            // can re-queue the run and MafWorkflowWorker re-picks it by stage; Status=Failed marks the failure.
            run.FinishedAt = DateTime.UtcNow;
        }, cancellationToken);
        _progress.Report(workflowRunId, "error", "Workflow thất bại.", error);
    }

    private async Task UpdateRunAsync(Guid workflowRunId, Action<Domain.WorkflowRun> mutate, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.WorkflowRuns.FirstOrDefaultAsync(r => r.Id == workflowRunId, cancellationToken);
        if (run is null)
            return;
        mutate(run);
        await db.SaveChangesAsync(cancellationToken);
    }
}
