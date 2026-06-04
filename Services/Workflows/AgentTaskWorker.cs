using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Workflows;

public class AgentTaskWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentTaskWorker> _logger;

    public AgentTaskWorker(IServiceScopeFactory scopeFactory, ILogger<AgentTaskWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextQueuedTaskAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed while processing queued workflow agent tasks.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task ProcessNextQueuedTaskAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<WorkflowStepDispatcher>();
        var stateApplier = scope.ServiceProvider.GetRequiredService<WorkflowStateApplier>();

        var task = await db.AgentTasks
            .Include(x => x.WorkflowRun)
            .Include(x => x.Project)
            .Where(x => x.Status == AgentTaskStatus.Queued)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (task == null)
            return;

        if (task.AgentId == null)
        {
            await stateApplier.FailAsync(task, "No agent is assigned to this task.", cancellationToken);
            return;
        }

        var previousTasks = await db.AgentTasks
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == task.WorkflowRunId && x.Id != task.Id)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        var context = new WorkflowExecutionContext
        {
            WorkflowRun = task.WorkflowRun,
            CurrentTask = task,
            Project = task.Project,
            PreviousTasks = previousTasks
        };

        var handler = dispatcher.Resolve(context);
        if (handler == null)
        {
            await stateApplier.FailAsync(task, $"No workflow step handler can process task type '{task.Type}'.", cancellationToken);
            return;
        }

        try
        {
            task.Status = AgentTaskStatus.Running;
            task.StartedAt = DateTime.UtcNow;
            task.Attempt += 1;
            task.WorkflowRun.Status = WorkflowRunStatus.Running;
            task.WorkflowRun.StartedAt ??= DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            var result = await handler.ExecuteAsync(context, cancellationToken);
            await stateApplier.ApplyAsync(context, result, cancellationToken);
        }
        catch (Exception ex)
        {
            await stateApplier.FailAsync(task, ex.Message, CancellationToken.None);
        }
    }
}
