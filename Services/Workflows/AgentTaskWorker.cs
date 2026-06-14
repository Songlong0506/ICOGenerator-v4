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
    // Markers and placeholder shared between the workspace seeding and the agent
    // prompt so the two can never drift apart (drift was the original cause of the
    // "poc-demo.html identical to template" bug). The start marker text MUST match
    // the literal line in Prompts/Design/poc-template.html.
    private const string PocContentStartMarker = "<!-- POC_CONTENT_START : replace everything below with the feature UI -->";
    private const string PocContentEndMarker = "<!-- POC_CONTENT_END -->";
    // Kept deliberately short so a weak model can reproduce it verbatim as ReplaceInFile's
    // oldText. Must stay unique in the file (the template only uses *_START / *_END).
    private const string PocContentPlaceholder = "<!-- POC_CONTENT -->";

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
- Vùng nội dung tính năng trong file hiện chỉ là ĐÚNG MỘT dòng placeholder duy nhất:
  {PocContentPlaceholder}
- Dùng tool ReplaceInFile ĐÚNG MỘT LẦN trên '03_Implementation/poc-demo.html':
    - oldText = chính xác chuỗi placeholder ở trên (copy nguyên văn).
    - newText = HTML giao diện của tính năng theo AI Design Spec.
  Hai marker "{PocContentStartMarker}" và "{PocContentEndMarker}" nằm NGOÀI placeholder nên sẽ tự được giữ nguyên.
- Dùng đúng các class có sẵn: card, card-grid, card-title, card-body, tile, tile-value, tile-label, btn, btn-outline, btn-ghost, table, field, input, select, textarea, badge, badge-green, badge-gray, row, stack, muted.
- File phải TỰ CHỨA (self-contained): KHÔNG link/nhúng CSS hay JS framework bên ngoài (không Angular/Material/Bootstrap...). Chỉ dùng CSS/JS đã có sẵn trong file.
- TUYỆT ĐỐI KHÔNG sửa <head>/<style>, <script>, cấu trúc shell (.supergraphic, .sidebar, .topbar) hay 2 popup User/Imprint.
- KHÔNG dùng RunCommand/grep. Sau khi ReplaceInFile trả "File updated", trả final result NGAY, KHÔNG đọc lại file.

Kết quả: chỉnh sửa tại chỗ file 03_Implementation/poc-demo.html (chỉ vùng giữa 2 marker).

# AI Design Spec

{task.Input}
""",
                maxSteps: 10,
                onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail),
                // The only required change is replacing the placeholder in poc-demo.html.
                // Stop the moment that edit succeeds so the agent doesn't keep editing the
                // shell and hit the step limit with the POC already done.
                stopWhen: (toolName, observation) =>
                    toolName.Equals("ReplaceInFile", StringComparison.OrdinalIgnoreCase)
                    && observation.Contains("File updated", StringComparison.OrdinalIgnoreCase)
                    && observation.Contains("poc-demo.html", StringComparison.OrdinalIgnoreCase));

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
                await SeedPocDemoAsync(templateSrc, resolver.GetMockupPath(project.Name));
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

        var startIdx = template.IndexOf(PocContentStartMarker, StringComparison.Ordinal);
        var endIdx = template.IndexOf(PocContentEndMarker, StringComparison.Ordinal);

        if (startIdx < 0 || endIdx <= startIdx)
        {
            // Markers missing/malformed: fall back to a raw copy so we never lose the file.
            File.Copy(templateSrc, demoPath, overwrite: true);
            return;
        }

        var afterStart = startIdx + PocContentStartMarker.Length;
        var seeded = template[..afterStart]
            + "\n                    " + PocContentPlaceholder + "\n                    "
            + template[endIdx..];

        await File.WriteAllTextAsync(demoPath, seeded);
    }
}