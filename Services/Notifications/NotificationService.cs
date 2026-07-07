using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Notifications;

/// <summary>
/// Hiện thực <see cref="INotificationService"/>: xác định người nhận đủ điều kiện theo quyền phù hợp với
/// loại thông báo (workflow ⇒ <see cref="AppPermission.DeliveryAdvance"/>; eval ⇒
/// <see cref="AppPermission.EvalView"/>) rồi <c>Add</c> một <see cref="Notification"/> cho mỗi người
/// vào DbContext hiện hành. Không SaveChanges (xem hợp đồng ở interface). Toàn bộ bọc try/catch fail-open.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly AppDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(AppDbContext db, IPermissionService permissions, ILogger<NotificationService> logger)
    {
        _db = db;
        _permissions = permissions;
        _logger = logger;
    }

    public Task NotifyGateOpenedAsync(WorkflowRun run, string nextStepTitle, CancellationToken cancellationToken = default) =>
        CreateForEligibleAsync(run, NotificationType.GateAwaitingApproval,
            "Chờ duyệt bước delivery",
            $"Một bước đã xong — chờ bạn duyệt để sang: {nextStepTitle}.",
            cancellationToken);

    public Task NotifyRunCompletedAsync(WorkflowRun run, CancellationToken cancellationToken = default) =>
        CreateForEligibleAsync(run, NotificationType.WorkflowCompleted,
            "Workflow hoàn tất",
            "Quy trình giao hàng đã chạy xong tất cả các bước.",
            cancellationToken);

    public Task NotifyRunFailedAsync(WorkflowRun run, string? error, CancellationToken cancellationToken = default) =>
        CreateForEligibleAsync(run, NotificationType.WorkflowFailed,
            "Workflow thất bại",
            string.IsNullOrWhiteSpace(error) ? "Quy trình giao hàng đã dừng vì lỗi — cần xem lại." : $"Quy trình dừng vì lỗi: {Truncate(error, 300)}",
            cancellationToken);

    public async Task NotifyEvalRegressionAsync(EvalRun run, double delta, double threshold, CancellationToken cancellationToken = default)
    {
        try
        {
            var recipients = await ResolveRecipientsAsync(AppPermission.EvalView, cancellationToken);
            if (recipients.Count == 0)
                return;

            var runLabel = string.IsNullOrWhiteSpace(run.Note) ? run.TargetModelName : run.Note;
            var message =
                $"Run \"{Truncate(runLabel, 120)}\" đạt {run.AverageScore:0.00} — tụt {Math.Abs(delta):0.00} điểm " +
                $"so với run trước trên các scenario chung (ngưỡng cảnh báo {threshold:0.00}). Prompt/model có thể vừa hỏng.";

            foreach (var username in recipients)
            {
                _db.Notifications.Add(new Notification
                {
                    RecipientUsername = username,
                    Type = NotificationType.EvalRegression,
                    Title = "Prompt eval tụt điểm",
                    Message = message,
                    Link = "/Evals"
                });
            }
        }
        catch (Exception ex)
        {
            // Fail-open: một thông báo hỏng không được làm gãy việc chốt kết quả run eval.
            _logger.LogWarning(ex, "Không tạo được thông báo hồi quy cho eval run {RunId}.", run.Id);
        }
    }

    private async Task CreateForEligibleAsync(WorkflowRun run, NotificationType type, string title, string message, CancellationToken cancellationToken)
    {
        try
        {
            var recipients = await ResolveRecipientsAsync(AppPermission.DeliveryAdvance, cancellationToken);
            if (recipients.Count == 0)
                return;

            var projectName = await _db.Projects
                .Where(p => p.Id == run.ProjectId)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(cancellationToken);

            foreach (var username in recipients)
            {
                _db.Notifications.Add(new Notification
                {
                    RecipientUsername = username,
                    Type = type,
                    Title = title,
                    Message = message,
                    ProjectId = run.ProjectId,
                    ProjectName = projectName,
                    WorkflowRunId = run.Id,
                    Link = $"/AgentDashboard?projectId={run.ProjectId}"
                });
            }
        }
        catch (Exception ex)
        {
            // Fail-open: một thông báo hỏng không được làm gãy workflow.
            _logger.LogWarning(ex, "Không tạo được thông báo cho workflow run {RunId}.", run.Id);
        }
    }

    // Người nhận = user đang hoạt động thuộc role CÓ quyền tương ứng loại thông báo. Bảng user nhỏ
    // (seed vài tài khoản) nên duyệt trực tiếp; kết quả kiểm tra quyền được cache trong PermissionService.
    private async Task<IReadOnlyList<string>> ResolveRecipientsAsync(AppPermission permission, CancellationToken cancellationToken)
    {
        var activeUsers = await _db.AppUsers
            .Where(u => u.IsActive)
            .Select(u => new { u.Username, u.Role })
            .ToListAsync(cancellationToken);

        var qualifyingRoles = new HashSet<UserRole>();
        foreach (var role in activeUsers.Select(u => u.Role).Distinct())
        {
            var granted = await _permissions.GetGrantedAsync(role, cancellationToken);
            if (granted.Contains(permission))
                qualifyingRoles.Add(role);
        }

        return activeUsers
            .Where(u => qualifyingRoles.Contains(u.Role) && !string.IsNullOrWhiteSpace(u.Username))
            .Select(u => u.Username)
            .Distinct()
            .ToList();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
