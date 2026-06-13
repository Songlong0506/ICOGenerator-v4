using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Workflows;

public class AgentTaskWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentTaskWorker> _logger;
    private readonly IWorkflowProgressReporter _progress;

    public AgentTaskWorker(IServiceScopeFactory scopeFactory, ILogger<AgentTaskWorker> logger, IWorkflowProgressReporter progress)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _progress = progress;
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

        var task = await db.AgentTasks
            .Include(x => x.WorkflowRun)
            .Where(x => x.Status == AgentTaskStatus.Queued)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (task == null)
            return;

        if (task.AgentId == null)
        {
            _progress.Report(task.WorkflowRunId, "error", "Không có agent nào được gán cho task này.");
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

            _progress.Report(task.WorkflowRunId, "start", $"Bắt đầu task: {task.Title}" + (task.Attempt > 1 ? $" (lần thử {task.Attempt})" : ""));

            if (task.Type == AgentTaskType.RequirementAnalysis)
            {
                await RunRequirementDraftAsync(scope, task);

                task.Status = AgentTaskStatus.Completed;
                task.Output = "Requirement documents generated/updated.";
                task.FinishedAt = DateTime.UtcNow;
                task.WorkflowRun.Status = WorkflowRunStatus.Completed;
                task.WorkflowRun.CurrentStage = WorkflowStageKey.Completed;
                task.WorkflowRun.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            // POC template chỉ cần cho bước Implementation.
            if (task.Type == AgentTaskType.Implementation)
            {
                _progress.Report(task.WorkflowRunId, "setup", "Chuẩn bị workspace và template POC…");
                await EnsureDesignAssetsAsync(scope, db, task.ProjectId);
            }

            var promptBuilder = scope.ServiceProvider.GetRequiredService<WorkflowTaskPromptBuilder>();
            var prompt = promptBuilder.Build(task.Type, task.Input);

            var output = await agentRunService.RunAsync(
                task.ProjectId,
                task.AgentId.Value,
                prompt,
                onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail));

            task.Status = AgentTaskStatus.Completed;
            task.Output = output;
            task.FinishedAt = DateTime.UtcNow;

            // HAND-OFF: hỏi pipeline "bước kế là gì?". Hết bước → kết thúc workflow;
            // còn bước → enqueue task cho vai kế, lấy output làm input. Worker vẫn
            // generic: không biết gì về Tech Lead/Dev/Tester, chỉ chạy task → hỏi bước kế.
            var next = DeliveryPipeline.Next(task.WorkflowRun.CurrentStage);
            if (next is null)
            {
                _progress.Report(task.WorkflowRunId, "completed", "Workflow hoàn tất — tất cả các bước đã xong.");
                task.WorkflowRun.Status = WorkflowRunStatus.Completed;
                task.WorkflowRun.CurrentStage = WorkflowStageKey.Completed;
                task.WorkflowRun.FinishedAt = DateTime.UtcNow;
            }
            else
            {
                var nextAgent = await db.Agents
                    .FirstOrDefaultAsync(a => a.RoleKey == next.Role, cancellationToken);

                task.WorkflowRun.CurrentStage = next.Stage;
                db.AgentTasks.Add(new AgentTask
                {
                    WorkflowRunId = task.WorkflowRunId,
                    ProjectId = task.ProjectId,
                    AgentId = nextAgent?.Id,
                    Type = next.TaskType,
                    Status = AgentTaskStatus.Queued,
                    Title = next.Title,
                    Input = output ?? string.Empty
                });
                _progress.Report(task.WorkflowRunId, "handoff", $"Bàn giao sang bước: {next.Title}");
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _progress.Report(task.WorkflowRunId, "error", "Task thất bại.", ex.Message);
            task.Status = AgentTaskStatus.Failed;
            task.Error = ex.Message;
            task.FinishedAt = DateTime.UtcNow;
            task.WorkflowRun.Status = WorkflowRunStatus.Failed;
            task.WorkflowRun.CurrentStage = WorkflowStageKey.Failed;
            task.WorkflowRun.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task RunRequirementDraftAsync(IServiceScope scope, AgentTask task)
    {
        var baService = scope.ServiceProvider.GetRequiredService<BARequirementService>();

        await baService.GenerateOrUpdateDraftAsync(
            task.ProjectId,
            onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail));

        _progress.Report(task.WorkflowRunId, "completed", "Đã tạo/cập nhật tài liệu requirement.");
    }

    private async Task EnsureDesignAssetsAsync(IServiceScope scope, AppDbContext db, Guid projectId)
    {
        try
        {
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId);
            if (project == null)
                return;

            var resolver = scope.ServiceProvider.GetRequiredService<WorkspacePathResolver>();
            var implDir = Path.GetDirectoryName(resolver.GetMockupPath(project.Name));
            if (string.IsNullOrWhiteSpace(implDir))
                return;

            Directory.CreateDirectory(implDir);

            var sourceDir = Path.Combine(AppContext.BaseDirectory, "Prompts", "Design");
            foreach (var name in new[] { "poc-template.html" })
            {
                var src = Path.Combine(sourceDir, name);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(implDir, name), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy POC design assets into the workspace.");
        }
    }
}