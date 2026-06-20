using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Workflows;

public class AgentTaskWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentTaskWorker> _logger;
    private readonly IWorkflowProgressReporter _progress;
    private readonly IWebHostEnvironment _environment;

    public AgentTaskWorker(IServiceScopeFactory scopeFactory, ILogger<AgentTaskWorker> logger, IWorkflowProgressReporter progress, IWebHostEnvironment environment)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _progress = progress;
        _environment = environment;
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed while processing queued workflow agent tasks.");
            }

            // Catch the shutdown cancellation here instead of letting it escape
            // ExecuteAsync (which the host treats as a crash); shutdown becomes a clean exit.
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
                await RunRequirementDraftAsync(scope, task, cancellationToken);

                task.Status = AgentTaskStatus.Completed;
                task.Output = "Requirement documents generated/updated.";
                task.FinishedAt = DateTime.UtcNow;
                task.WorkflowRun.Status = WorkflowRunStatus.Completed;
                task.WorkflowRun.CurrentStage = WorkflowStageKey.Completed;
                task.WorkflowRun.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            // POC template chỉ cần cho bước POC preview.
            if (task.Type == AgentTaskType.PocPreview)
            {
                _progress.Report(task.WorkflowRunId, "setup", "Chuẩn bị workspace và template POC…");
                await EnsureDesignAssetsAsync(scope, db, task.ProjectId);
            }

            var promptBuilder = scope.ServiceProvider.GetRequiredService<WorkflowTaskPromptBuilder>();
            var prompt = promptBuilder.Build(task.Type, task.Input);
            var currentStep = DeliveryPipeline.Find(task.WorkflowRun.CurrentStage);
            // BugFix là việc sửa code (đa file) chạy bên trong cổng của bước hiện tại (vd Testing); nó cần
            // hạn mức bước của cấu hình rework, không phải MaxSteps (nhỏ) của bước kiểm thử.
            var maxSteps = task.Type == AgentTaskType.BugFix
                ? (currentStep?.Rework?.MaxSteps ?? 24)
                : (currentStep?.MaxSteps ?? 6);

            // Riêng bước POC bắt buộc gọi SetPocContent đúng một lần; dừng NGAY khi thành công để
            // agent khỏi loay hoay chạm giới hạn bước. Các bước khác kết thúc bằng "final" như thường.
            Func<string, string, bool>? stopWhen = task.Type == AgentTaskType.PocPreview
                ? (toolName, observation) =>
                    toolName.Equals(nameof(WorkspaceTools.SetPocContent), StringComparison.OrdinalIgnoreCase)
                    && observation.Contains("POC content updated", StringComparison.OrdinalIgnoreCase)
                : null;

            var output = await agentRunService.RunAsync(
                task.ProjectId,
                task.AgentId.Value,
                prompt,
                maxSteps,
                onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail),
                stopWhen: stopWhen,
                onToken: token => _progress.ReportToken(task.WorkflowRunId, token),
                workflowRunId: task.WorkflowRunId,
                cancellationToken: cancellationToken);

            // Nếu agent đạt giới hạn số bước mà chưa hoàn tất (chưa gọi tool tạo deliverable),
            // coi là thất bại để báo lỗi rõ thay vì ghi nhận Completed sai lệch.
            if (string.Equals(output, AgentRunService.MaxStepsReachedResult, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    task.Type == AgentTaskType.PocPreview
                        ? "Agent đạt giới hạn số bước mà chưa tạo được POC (chưa gọi SetPocContent thành công)."
                        : $"Agent đạt giới hạn số bước mà chưa hoàn tất bước '{task.Title}' (chưa gọi tool ghi kết quả).");

            task.Status = AgentTaskStatus.Completed;
            task.Output = output;
            task.FinishedAt = DateTime.UtcNow;

            if (task.Type == AgentTaskType.BugFix)
            {
                // VÒNG LẶP CHẤT LƯỢNG: Developer vừa sửa lỗi xong → tự động chạy lại bước kiểm thử
                // (re-verify) thay vì nhảy bước. CurrentStage giữ nguyên ở bước có rework (Testing).
                await EnqueueRetestAsync(db, task, output, cancellationToken);
            }
            else
            {
                // CỔNG DUYỆT: bước chạy xong thì DỪNG chờ người duyệt thay vì tự enqueue bước kế
                // (hoặc tự hoàn tất). Bước kế / việc hoàn tất chỉ xảy ra khi user bấm duyệt
                // (ApproveStageUseCase). CurrentStage giữ nguyên ở bước vừa xong để biết đang chờ duyệt cái gì.
                var next = DeliveryPipeline.Next(task.WorkflowRun.CurrentStage);
                _progress.Report(task.WorkflowRunId, "completed", next is null
                    ? $"Bước \"{task.Title}\" xong — chờ bạn duyệt để HOÀN TẤT workflow."
                    : $"Bước \"{task.Title}\" xong — chờ bạn duyệt để sang: {next.Title}.");
                task.WorkflowRun.Status = WorkflowRunStatus.WaitingForHuman;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // App shutting down mid-task: leave it Running and let the startup reaper re-queue it on the
            // next launch, instead of recording a misleading Failed for work that was merely interrupted.
            throw;
        }
        catch (Exception ex)
        {
            _progress.Report(task.WorkflowRunId, "error", "Task thất bại.", ex.Message);
            // Mark Failed in a FRESH scope: the run's DbContext may already be faulted (e.g. the
            // exception was a DbUpdateException), so reusing it to save the failure could itself throw
            // and leave the task stuck non-terminal.
            await MarkTaskFailedAsync(task.Id, ex.Message);
        }
    }

    // Loads the task in its own scope/DbContext and marks it (and its run) Failed, independent of any
    // faulted context from the failed run. Best-effort: a failure here is logged, not rethrown.
    private async Task MarkTaskFailedAsync(Guid taskId, string error)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var task = await db.AgentTasks.Include(x => x.WorkflowRun).FirstOrDefaultAsync(x => x.Id == taskId);
            if (task == null)
                return;

            var now = DateTime.UtcNow;
            task.Status = AgentTaskStatus.Failed;
            task.Error = error;
            task.FinishedAt = now;
            task.WorkflowRun.Status = WorkflowRunStatus.Failed;
            task.WorkflowRun.CurrentStage = WorkflowStageKey.Failed;
            task.WorkflowRun.FinishedAt = now;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark agent task {TaskId} as failed.", taskId);
        }
    }

    // Sau một lượt BugFix thành công: tạo lại task cho CHÍNH bước có rework (vd Tester/Testing) để
    // xác minh bản sửa. Input = tóm tắt việc Dev vừa làm. CurrentStage không đổi, nên sau khi chạy lại
    // xong workflow lại dừng đúng ở cổng duyệt của bước đó (user có thể duyệt hoàn tất hoặc gửi lại tiếp).
    private async Task EnqueueRetestAsync(AppDbContext db, AgentTask bugfixTask, string output, CancellationToken cancellationToken)
    {
        var step = DeliveryPipeline.Find(bugfixTask.WorkflowRun.CurrentStage);
        if (step is null)
        {
            // Phòng thủ: không xác định được bước để chạy lại → hoàn tất để không kẹt vô hạn.
            bugfixTask.WorkflowRun.Status = WorkflowRunStatus.Completed;
            bugfixTask.WorkflowRun.CurrentStage = WorkflowStageKey.Completed;
            bugfixTask.WorkflowRun.FinishedAt = DateTime.UtcNow;
            return;
        }

        var verifier = await db.Agents.FirstOrDefaultAsync(a => a.RoleKey == step.Role, cancellationToken);
        db.AgentTasks.Add(new AgentTask
        {
            WorkflowRunId = bugfixTask.WorkflowRunId,
            ProjectId = bugfixTask.ProjectId,
            AgentId = verifier?.Id,
            Type = step.TaskType,
            Status = AgentTaskStatus.Queued,
            Title = step.Title,
            Input = output
        });

        bugfixTask.WorkflowRun.Status = WorkflowRunStatus.Queued; // worker sẽ chạy lại bước kiểm thử
        _progress.Report(bugfixTask.WorkflowRunId, "completed", $"Đã sửa lỗi — chạy lại bước \"{step.Title}\" để kiểm chứng.");
    }

    private async Task RunRequirementDraftAsync(IServiceScope scope, AgentTask task, CancellationToken cancellationToken)
    {
        var baService = scope.ServiceProvider.GetRequiredService<BARequirementService>();

        await baService.GenerateOrUpdateDraftAsync(
            task.ProjectId,
            onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail),
            onToken: token => _progress.ReportToken(task.WorkflowRunId, token),
            workflowRunId: task.WorkflowRunId,
            cancellationToken: cancellationToken);

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
            var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
            var implDir = Path.GetDirectoryName(resolver.GetMockupPath(projectKey));
            if (string.IsNullOrWhiteSpace(implDir))
                return;

            Directory.CreateDirectory(implDir);

            // Resolve from ContentRootPath so this worker and PromptTemplateService share the same "Prompts" root (BaseDirectory = bin output diverged from project root).
            var sourceDir = Path.Combine(_environment.ContentRootPath, "Prompts", "Design");
            foreach (var name in new[] { "poc-template.html" })
            {
                var src = Path.Combine(sourceDir, name);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(implDir, name), overwrite: true);
            }

            // Pre-seed poc-demo.html so the dev agent edits only the content region, not re-emitting the whole shell (saves tokens per run).
            // The marker region is collapsed to a SINGLE placeholder so one deterministic ReplaceInFile works, vs reproducing the ~160-line block verbatim (always failed "Old text not found").
            var templateSrc = Path.Combine(sourceDir, "poc-template.html");
            if (File.Exists(templateSrc))
                await SeedPocDemoAsync(templateSrc, resolver.GetMockupPath(projectKey));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy POC design assets into the workspace.");
        }
    }

    // Copies the template into poc-demo.html, replacing the POC_CONTENT region with a placeholder but keeping the markers as a stable editable region.
    private static async Task SeedPocDemoAsync(string templateSrc, string demoPath)
    {
        var template = await File.ReadAllTextAsync(templateSrc);
        var seeded = PocTemplate.SeedFromTemplate(template);

        if (seeded == null)
        {
            // Markers missing/malformed: fall back to a raw copy so we never lose the file.
            File.Copy(templateSrc, demoPath, overwrite: true);
            return;
        }

        await File.WriteAllTextAsync(demoPath, seeded);
    }
}