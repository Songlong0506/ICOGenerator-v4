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
                // Normal shutdown.
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

            _progress.Report(task.WorkflowRunId, "setup", "Chuẩn bị workspace và template POC…");

            await EnsureDesignAssetsAsync(scope, db, task.ProjectId);

            var output = await agentRunService.RunAsync(
                task.ProjectId,
                task.AgentId.Value,
                $"""
User đã approve requirement.

Chỉ sử dụng AI Design Spec bên dưới để generate code.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.

YÊU CẦU GIAO DIỆN (bắt buộc — để POC đồng bộ với template có sẵn):
- File '03_Implementation/poc-demo.html' ĐÃ TỒN TẠI sẵn (là bản sao của shell template: <head> + <style>, <script>, sidebar/topbar, 2 popup User/Imprint đều đã hoàn chỉnh). KHÔNG cần đọc lại file và KHÔNG ghi đè cả file bằng WriteFile.
- Dùng tool SetPocContent ĐÚNG MỘT LẦN, truyền ĐỦ 4 tham số sau để POC khớp với tính năng (KHÔNG để nguyên mặc định của template):
  • content (bắt buộc): HTML giao diện của tính năng theo AI Design Spec — CHỈ phần nội dung bên trong, KHÔNG kèm <html>/<head>/<body>/sidebar/topbar.
  • appName (bắt buộc): tên ứng dụng/sản phẩm theo AI Design Spec, hiển thị ở đầu sidebar và tiêu đề tab. TUYỆT ĐỐI KHÔNG để mặc định "App Name".
  • breadcrumb (bắt buộc): breadcrumb của màn hình chính, vd "Home > Orders".
  • navItems (bắt buộc): menu sidebar bên trái — mảng các mục, mỗi mục có "label" và tùy chọn "children" (mảng tên mục con) cho nhóm xổ xuống. Đặt theo đúng các màn hình/chức năng thật trong AI Design Spec; TUYỆT ĐỐI KHÔNG dùng "Overview/Module A/Module B/Settings" của template. Xem ví dụ JSON ở phần hướng dẫn tool trong system prompt.
- Hệ thống sẽ tự đặt content vào vùng giữa 2 marker, đổi App Name + tiêu đề + breadcrumb và dựng lại menu sidebar từ navItems, giữ nguyên toàn bộ phần còn lại của shell (style/script/topbar/popup).
- Dùng đúng các class có sẵn cho content: card, card-grid, card-title, card-body, tile, tile-value, tile-label, btn, btn-outline, btn-ghost, table, field, input, select, textarea, badge, badge-green, badge-gray, row, stack, muted.
- Nội dung phải TỰ CHỨA: KHÔNG link/nhúng CSS hay JS framework bên ngoài (không Angular/Material/Bootstrap...). Chỉ dùng CSS/JS đã có sẵn trong file.
- KHÔNG dùng ReplaceInFile/WriteFile/RunCommand/grep cho việc này. Sau khi SetPocContent trả "POC content updated", trả final result NGAY, KHÔNG đọc lại file.

Kết quả: content tính năng + App Name + breadcrumb + menu sidebar được cập nhật trong 03_Implementation/poc-demo.html.

# AI Design Spec

{task.Input}
""",
                maxSteps: 10,
                onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail),
                // The only required action is one SetPocContent call. Stop the moment it
                // succeeds so the agent doesn't keep poking the file and hit the step limit
                // with the POC already done.
                stopWhen: (toolName, observation) =>
                    toolName.Equals(nameof(WorkspaceTools.SetPocContent), StringComparison.OrdinalIgnoreCase)
                    && observation.Contains("POC content updated", StringComparison.OrdinalIgnoreCase),
                cancellationToken: cancellationToken);

            // The agent ran out of steps without ever landing a successful SetPocContent
            // (the stopWhen above never fired). The POC was NOT produced, so fail the task
            // instead of recording it as Completed with a misleading output.
            if (string.Equals(output, AgentRunService.MaxStepsReachedResult, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Agent đạt giới hạn số bước mà chưa tạo được POC (chưa gọi SetPocContent thành công).");

            _progress.Report(task.WorkflowRunId, "completed", "Task hoàn tất — POC đã được tạo.");

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

    private async Task RunRequirementDraftAsync(IServiceScope scope, AgentTask task, CancellationToken cancellationToken)
    {
        var baService = scope.ServiceProvider.GetRequiredService<BARequirementService>();

        await baService.GenerateOrUpdateDraftAsync(
            task.ProjectId,
            onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail),
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

            // Resolve prompt assets from ContentRootPath so this worker and
            // PromptTemplateService read from the same "Prompts" root (they previously
            // diverged: BaseDirectory = bin output vs ContentRootPath = project root).
            var sourceDir = Path.Combine(_environment.ContentRootPath, "Prompts", "Design");
            foreach (var name in new[] { "poc-template.html" })
            {
                var src = Path.Combine(sourceDir, name);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(implDir, name), overwrite: true);
            }

            // Pre-seed poc-demo.html from the template so the dev agent only edits the
            // content region (between the POC_CONTENT markers) instead of reading and
            // re-emitting the whole shell (head <style>, script, sidebar/topbar) — this
            // is the bulk of the boilerplate and removing the round-trip saves a large
            // amount of tokens per POC run. Overwriting resets a clean baseline, matching
            // the previous behaviour where the agent recreated the file each run.
            //
            // The region between the markers is collapsed to a SINGLE short placeholder
            // line so the agent can swap it in with one deterministic ReplaceInFile call,
            // rather than having to reproduce the whole ~160-line block verbatim (which
            // always failed with "Old text not found" and left the file unchanged).
            var templateSrc = Path.Combine(sourceDir, "poc-template.html");
            if (File.Exists(templateSrc))
                await SeedPocDemoAsync(templateSrc, resolver.GetMockupPath(projectKey));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy POC design assets into the workspace.");
        }
    }

    // Copies the template into poc-demo.html, replacing everything between the two
    // POC_CONTENT markers with a single placeholder line. The markers themselves are
    // preserved so the generated POC keeps a stable, editable content region.
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