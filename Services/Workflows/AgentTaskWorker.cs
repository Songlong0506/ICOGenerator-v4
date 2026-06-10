using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Prompts;
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
        var agentRunService = scope.ServiceProvider.GetRequiredService<AgentRunService>();
        var promptTemplateService = scope.ServiceProvider.GetRequiredService<PromptTemplateService>();

        var task = await db.AgentTasks
            .Include(x => x.WorkflowRun)
            .Where(x => x.Status == AgentTaskStatus.Queued)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (task == null)
            return;

        if (task.AgentId == null)
        {
            task.Status = AgentTaskStatus.Failed;
            task.Error = "No agent is assigned to this task.";
            task.FinishedAt = DateTime.UtcNow;
            task.WorkflowRun.Status = WorkflowRunStatus.Failed;
            task.WorkflowRun.CurrentStage = WorkflowStageKey.Failed;
            task.WorkflowRun.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
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

            var pocStyleGuide = promptTemplateService.Get("Agents/poc-html-style.v1.md");

            var output = await agentRunService.RunAsync(
                task.ProjectId,
                task.AgentId.Value,
                $"""
User đã approve requirement.

Chỉ sử dụng AI Design Spec bên dưới để generate code.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.
Ghi file demo HTML vào đúng đường dẫn (relative): 03_Implementation/poc-demo.html
Bắt buộc tuân thủ Fixed POC HTML Style Guide. Nếu AI Design Spec không nói rõ layout/style, style guide là nguồn quyết định.

# Fixed POC HTML Style Guide

{pocStyleGuide}

# AI Design Spec

{task.Input}
""");

            task.Status = AgentTaskStatus.Completed;
            task.Output = output;
            task.FinishedAt = DateTime.UtcNow;
            task.WorkflowRun.Status = WorkflowRunStatus.Completed;
            task.WorkflowRun.CurrentStage = WorkflowStageKey.Completed;
            task.WorkflowRun.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            task.Status = AgentTaskStatus.Failed;
            task.Error = ex.Message;
            task.FinishedAt = DateTime.UtcNow;
            task.WorkflowRun.Status = WorkflowRunStatus.Failed;
            task.WorkflowRun.CurrentStage = WorkflowStageKey.Failed;
            task.WorkflowRun.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
