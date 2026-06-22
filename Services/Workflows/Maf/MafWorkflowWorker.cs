using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Workflows.Maf;

/// <summary>
/// Background driver for the opt-in MAF delivery engine. Polls for delivery <see cref="Domain.WorkflowRun"/>s
/// that are <see cref="WorkflowRunStatus.Queued"/> (a fresh start, an approved gate, or a retry/restart
/// recovery) and advances each one step via <see cref="MafDeliveryEngine"/>. Idle when the engine is off,
/// so the legacy <see cref="AgentTaskWorker"/> remains the default path.
///
/// Delivery runs are identified by their current stage; this deliberately excludes the requirement-draft
/// flow (which stays on the AgentTask queue), so the two workers never contend for the same run.
/// </summary>
public sealed class MafWorkflowWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MafDeliveryEngine _engine;
    private readonly MafWorkflowPolicy _policy;
    private readonly ILogger<MafWorkflowWorker> _logger;

    public MafWorkflowWorker(IServiceScopeFactory scopeFactory, MafDeliveryEngine engine, MafWorkflowPolicy policy, ILogger<MafWorkflowWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _engine = engine;
        _policy = policy;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Engine disabled: stay idle and let AgentTaskWorker own delivery.
        if (!_policy.UseMafEngine)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DriveNextQueuedRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed while driving a MAF delivery workflow run.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task DriveNextQueuedRunAsync(CancellationToken cancellationToken)
    {
        Guid? runId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            runId = await db.WorkflowRuns
                .Where(r => r.Status == WorkflowRunStatus.Queued && DeliveryPipeline.DeliveryStages.Contains(r.CurrentStage))
                .OrderBy(r => r.CreatedAt)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (runId is null)
            return;

        await _engine.DriveAsync(runId.Value, cancellationToken);
    }
}
