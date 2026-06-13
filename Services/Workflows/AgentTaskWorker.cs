using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Agents;
using ICOGenerator.Services.Artifacts;
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
- Trong thư mục 03_Implementation đã có sẵn file 'poc-template.html' — shell layout đã thiết kế (CSS trong <style>, tương tác trong <script>). Hãy đọc file này trước bằng tool.
- TẠO file 03_Implementation/poc-demo.html dựa trên poc-template.html: GIỮ NGUYÊN toàn bộ <head> (kể cả <style>), <script> ở cuối <body>, và phần shell: .supergraphic, .sidebar (gồm app name + nav + 2 item User/Imprint ở cuối) và .topbar (breadcrumb + logo Bosch).
- CHỈ thay nội dung nằm giữa "<!-- POC_CONTENT_START -->" và "<!-- POC_CONTENT_END -->" bằng UI của tính năng theo AI Design Spec, dùng đúng các class có sẵn: card, card-grid, card-title, card-body, tile, tile-value, tile-label, btn, btn-outline, btn-ghost, table, field, input, select, textarea, badge, badge-green, badge-gray, row, stack, muted.
- Có thể đổi App Name (.app-name), text breadcrumb, và các nav-item/nav-group cho khớp tính năng (mỗi nav-group có .nav-item + .nav-sub để mở/đóng). KHÔNG đổi cấu trúc shell, KHÔNG sửa <style> và <script>.
- File phải TỰ CHỨA (self-contained): KHÔNG link/nhúng CSS hay JS framework bên ngoài (không Angular/Material/Bootstrap...). Chỉ dùng CSS/JS đã có sẵn trong file.

Ghi kết quả vào (relative): 03_Implementation/poc-demo.html

# AI Design Spec

{task.Input}
""",
                onProgress: (kind, message, detail) => _progress.Report(task.WorkflowRunId, kind, message, detail));

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