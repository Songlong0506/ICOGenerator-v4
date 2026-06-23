using ICOGenerator.Contracts.Requirements;
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
                var outcome = await RunRequirementDraftAsync(scope, task, cancellationToken);

                task.Status = AgentTaskStatus.Completed;
                task.Output = outcome == RequirementDraftOutcome.NeedsMoreInfo
                    ? RequirementDraftMarkers.NeedsMoreInfo
                    : "Requirement documents generated/updated.";
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

            var project = await db.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == task.ProjectId, cancellationToken)
                ?? throw new InvalidOperationException($"Không tìm thấy project {task.ProjectId} cho task này.");

            // Bosch template: clone bộ khung chuẩn (.NET + Angular) vào workspace TRƯỚC khi Developer
            // hiện thực, để bước Implementation code THÊM vào skeleton thay vì dựng khung từ đầu.
            if (task.Type == AgentTaskType.Implementation && project.IsUseBoschTemplate)
            {
                _progress.Report(task.WorkflowRunId, "setup", "Bosch template: chuẩn bị skeleton (.NET + Angular)…");
                var seeder = scope.ServiceProvider.GetRequiredService<BoschTemplateSeeder>();
                var skeletonKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
                var seedSummary = await seeder.SeedAsync(skeletonKey, cancellationToken);
                _progress.Report(task.WorkflowRunId, "setup", $"Bosch skeleton: {seedSummary}");
            }

            var promptBuilder = scope.ServiceProvider.GetRequiredService<WorkflowTaskPromptBuilder>();
            var prompt = promptBuilder.Build(task.Type, task.Input, project.IsUseBoschTemplate);
            var maxSteps = DeliveryPipeline.Find(task.WorkflowRun.CurrentStage)?.MaxSteps ?? 6;

            // POC được dựng qua NHIỀU call (SetPocContent cho màn hình đầu, rồi các AppendPocContent cho
            // phần còn lại) để không call nào phải chứa cả trang và bị cắt do giới hạn token. Agent tự nối
            // hết các phần rồi kết thúc; mỗi call ghi thẳng ra đĩa nên phần đã dựng vẫn được giữ kể cả khi
            // chạm giới hạn bước.
            var output = await agentRunService.RunAsync(
                task.ProjectId,
                task.AgentId.Value,
                prompt,
                maxSteps,
                onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail),
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

            // Vòng tự sửa lỗi (Testing↔BugFix) là một CHU TRÌNH (không phải hand-off tuyến tính) nên
            // được xử lý riêng. Nếu task này không thuộc chu trình đó thì rơi về cổng duyệt tuyến tính.
            if (!await TryAdvanceTestFixCycleAsync(db, task, cancellationToken))
                AdvanceLinearPipeline(task);

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

    // Cổng duyệt tuyến tính: bước chạy xong thì DỪNG chờ người duyệt thay vì tự enqueue bước kế.
    // Bước kế chỉ được tạo khi user bấm duyệt (ApproveStageUseCase). CurrentStage giữ nguyên ở bước
    // vừa xong để biết đang chờ duyệt cái gì.
    private void AdvanceLinearPipeline(AgentTask task)
    {
        var next = DeliveryPipeline.Next(task.WorkflowRun.CurrentStage);
        if (next is null)
        {
            _progress.Report(task.WorkflowRunId, "completed", "Workflow hoàn tất — tất cả các bước đã xong.");
            CompleteRun(task.WorkflowRun);
        }
        else
        {
            _progress.Report(task.WorkflowRunId, "completed", $"Bước \"{task.Title}\" xong — chờ bạn duyệt để sang: {next.Title}.");
            task.WorkflowRun.Status = WorkflowRunStatus.WaitingForHuman;
        }
    }

    // Chu trình tự sửa lỗi quanh Testing — KHÔNG có cổng duyệt (đây là vòng tự động, set run về Queued
    // để worker tự nhặt bước kế). Trả về true nếu đã xử lý hand-off cho chu trình này; false để
    // AdvanceLinearPipeline lo (task không thuộc chu trình).
    //   • Testing FAIL còn ngạch  → giao Developer (BugFix).
    //   • Testing FAIL hết ngạch  → kết thúc run, báo còn lỗi để người xem lại.
    //   • Testing PASS/không rõ    → trả false (pipeline tuyến tính kết thúc run như cũ).
    //   • BugFix xong              → luôn chạy lại Testing để xác minh.
    private async Task<bool> TryAdvanceTestFixCycleAsync(AppDbContext db, AgentTask task, CancellationToken cancellationToken)
    {
        if (task.Type == AgentTaskType.Testing)
        {
            if (TestVerdictParser.Parse(task.Output) != TestVerdict.Fail)
                return false;

            var fixAttempts = await db.AgentTasks.CountAsync(
                t => t.WorkflowRunId == task.WorkflowRunId && t.Type == AgentTaskType.BugFix,
                cancellationToken);

            if (fixAttempts >= DeliveryPipeline.MaxBugFixAttempts)
            {
                _progress.Report(task.WorkflowRunId, "completed",
                    $"Vẫn còn lỗi sau {DeliveryPipeline.MaxBugFixAttempts} lần tự sửa — dừng vòng lặp, cần người xem lại báo cáo test.");
                CompleteRun(task.WorkflowRun);
                return true;
            }

            _progress.Report(task.WorkflowRunId, "completed",
                $"Test phát hiện lỗi — tự động giao Developer sửa (lần {fixAttempts + 1}/{DeliveryPipeline.MaxBugFixAttempts}).");
            return await EnqueueFollowUpAsync(db, task, DeliveryPipeline.BugFixStep, task.Output ?? string.Empty, cancellationToken);
        }

        if (task.Type == AgentTaskType.BugFix)
        {
            _progress.Report(task.WorkflowRunId, "completed", "Đã sửa lỗi — chạy lại Test để xác minh.");
            return await EnqueueFollowUpAsync(db, task, DeliveryPipeline.TestingStep, task.Output ?? string.Empty, cancellationToken);
        }

        return false;
    }

    // Enqueue task cho bước kế trong chu trình và đẩy run về Queued (worker tự nhặt — không cổng duyệt).
    // Thiếu agent cho vai cần thiết thì đánh Failed có thông báo rõ. Luôn trả true: đã xử lý hand-off.
    private async Task<bool> EnqueueFollowUpAsync(AppDbContext db, AgentTask previous, PipelineStep step, string input, CancellationToken cancellationToken)
    {
        var agentId = await db.Agents
            .Where(a => a.RoleKey == step.Role)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (agentId is null)
        {
            _progress.Report(previous.WorkflowRunId, "error", $"Không tìm thấy agent vai {step.Role} cho bước \"{step.Title}\".");
            previous.WorkflowRun.Status = WorkflowRunStatus.Failed;
            previous.WorkflowRun.CurrentStage = WorkflowStageKey.Failed;
            previous.WorkflowRun.FinishedAt = DateTime.UtcNow;
            return true;
        }

        db.AgentTasks.Add(new AgentTask
        {
            WorkflowRunId = previous.WorkflowRunId,
            ProjectId = previous.ProjectId,
            AgentId = agentId,
            Type = step.TaskType,
            Status = AgentTaskStatus.Queued,
            Title = step.Title,
            Input = input
        });

        previous.WorkflowRun.CurrentStage = step.Stage;
        previous.WorkflowRun.Status = WorkflowRunStatus.Queued;
        return true;
    }

    private static void CompleteRun(WorkflowRun run)
    {
        run.Status = WorkflowRunStatus.Completed;
        run.CurrentStage = WorkflowStageKey.Completed;
        run.FinishedAt = DateTime.UtcNow;
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

    private async Task<RequirementDraftOutcome> RunRequirementDraftAsync(IServiceScope scope, AgentTask task, CancellationToken cancellationToken)
    {
        var baService = scope.ServiceProvider.GetRequiredService<BARequirementService>();

        var outcome = await baService.GenerateOrUpdateDraftAsync(
            task.ProjectId,
            onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail),
            onToken: token => _progress.ReportToken(task.WorkflowRunId, token),
            workflowRunId: task.WorkflowRunId,
            cancellationToken: cancellationToken);

        _progress.Report(task.WorkflowRunId, "completed",
            outcome == RequirementDraftOutcome.NeedsMoreInfo
                ? "Cần bổ sung thông tin trước khi sinh tài liệu — xem câu hỏi trong khung chat."
                : "Đã tạo/cập nhật tài liệu requirement.");

        return outcome;
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