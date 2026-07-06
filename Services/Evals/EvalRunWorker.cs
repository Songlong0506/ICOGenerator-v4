using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Evals;

/// <summary>
/// BackgroundService chạy các EvalRun ở trạng thái Queued (cùng mẫu poll như AgentTaskWorker): một run
/// eval là N scenario × 2 lời gọi LLM — quá chậm cho một POST request, nên UI chỉ tạo run Queued rồi
/// poll tiến độ. Lúc khởi động, run còn Running là "mồ côi" sau restart ⇒ đánh Failed (eval không có cơ
/// chế resume giữa chừng; chạy lại là bấm nút mới, rẻ hơn resume nửa vời).
/// </summary>
public class EvalRunWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EvalRunWorker> _logger;

    public EvalRunWorker(IServiceScopeFactory scopeFactory, ILogger<EvalRunWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverOrphanedRunsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover orphaned eval runs at startup.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextQueuedRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed while processing queued eval runs.");
            }

            // Nuốt cancellation của shutdown tại đây (như AgentTaskWorker) để host coi là dừng sạch.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessNextQueuedRunAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var runId = await db.EvalRuns
            .Where(x => x.Status == EvalRunStatus.Queued)
            .OrderBy(x => x.CreatedAt)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (runId is not Guid id)
            return;

        var runner = scope.ServiceProvider.GetRequiredService<EvalRunnerService>();
        try
        {
            await runner.RunAsync(id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Lỗi bất ngờ mức run (runner đã tự nuốt lỗi từng scenario): đánh Failed để UI không treo "Running".
            _logger.LogError(ex, "Eval run {RunId} failed unexpectedly.", id);
            await MarkRunFailedAsync(id, ex.Message, cancellationToken);
        }
    }

    private async Task MarkRunFailedAsync(Guid runId, string error, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run = await db.EvalRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
            if (run == null || run.Status is EvalRunStatus.Completed or EvalRunStatus.Failed)
                return;

            run.Status = EvalRunStatus.Failed;
            run.Error = error;
            run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark eval run {RunId} as failed.", runId);
        }
    }

    private async Task RecoverOrphanedRunsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orphaned = await db.EvalRuns
            .Where(x => x.Status == EvalRunStatus.Running)
            .ToListAsync(cancellationToken);
        if (orphaned.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var run in orphaned)
        {
            run.Status = EvalRunStatus.Failed;
            run.Error = "Run bị gián đoạn bởi việc khởi động lại ứng dụng. Hãy chạy lại eval.";
            run.FinishedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
