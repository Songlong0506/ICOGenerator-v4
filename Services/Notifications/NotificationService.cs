using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Notifications;

/// <summary>
/// Hiện thực <see cref="INotificationService"/>: xác định người nhận đủ điều kiện (user hoạt động có quyền
/// <see cref="AppPermission.DeliveryAdvance"/>) rồi <c>Add</c> một <see cref="Notification"/> cho mỗi người
/// vào DbContext hiện hành. Không SaveChanges (xem hợp đồng ở interface). Toàn bộ bọc try/catch fail-open.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly AppDbContext _db;
    private readonly IPermissionService _permissions;
    private readonly IEnumerable<INotificationChannel> _channels;
    private readonly NotificationOptions _options;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        AppDbContext db,
        IPermissionService permissions,
        IEnumerable<INotificationChannel> channels,
        NotificationOptions options,
        ILogger<NotificationService> logger)
    {
        _db = db;
        _permissions = permissions;
        _channels = channels;
        _options = options;
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

    private async Task CreateForEligibleAsync(WorkflowRun run, NotificationType type, string title, string message, CancellationToken cancellationToken)
    {
        var relativeLink = $"/AgentDashboard?projectId={run.ProjectId}";
        string? projectName = null;
        IReadOnlyList<string> emailRecipients = Array.Empty<string>();

        // ----- Kênh in-app (chuông) + gom email cá nhân, TÔN TRỌNG tùy chọn của từng user. -----
        try
        {
            projectName = await _db.Projects
                .Where(p => p.Id == run.ProjectId)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(cancellationToken);

            var eligible = await ResolveEligibleUsersAsync(cancellationToken);

            // In-app: chỉ ghi cho user bật chuông VÀ chưa tắt loại sự kiện này. Chỉ Add, người gọi lưu.
            foreach (var user in eligible.Where(u => u.NotifyInApp && WantsType(u, type)))
            {
                _db.Notifications.Add(new Notification
                {
                    RecipientUsername = user.Username,
                    Type = type,
                    Title = title,
                    Message = message,
                    ProjectId = run.ProjectId,
                    ProjectName = projectName,
                    WorkflowRunId = run.Id,
                    Link = relativeLink
                });
            }

            // Email cá nhân: user bật email + có địa chỉ + chưa tắt loại sự kiện này (ngoài danh sách To của admin).
            emailRecipients = eligible
                .Where(u => u.NotifyByEmail && WantsType(u, type) && !string.IsNullOrWhiteSpace(u.Email))
                .Select(u => u.Email!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            // Fail-open: lỗi ghi in-app không được làm gãy workflow, và cũng không chặn kênh ngoài bên dưới.
            _logger.LogWarning(ex, "Không tạo được thông báo in-app cho workflow run {RunId}.", run.Id);
        }

        // ----- Kênh NGOÀI (Teams/email): độc lập với in-app, opt-in, fail-open. Teams broadcast (không theo
        // pref user); email gộp danh sách To của admin với email cá nhân đã opt-in ở trên. -----
        await DispatchExternalAsync(
            new NotificationMessage(type, title, message, projectName, ToAbsoluteUrl(relativeLink), emailRecipients),
            cancellationToken);
    }

    // Loại sự kiện này có nằm trong các loại user muốn nhận không.
    private static bool WantsType(EligibleUser user, NotificationType type) => type switch
    {
        NotificationType.GateAwaitingApproval => user.NotifyOnGate,
        NotificationType.WorkflowCompleted => user.NotifyOnCompleted,
        NotificationType.WorkflowFailed => user.NotifyOnFailed,
        _ => true
    };

    // Fan-out tới các kênh ngoài ĐANG BẬT. Mặc định không kênh nào bật ⇒ vòng lặp rỗng, không overhead.
    // Mỗi kênh tự fail-open; bọc thêm một lớp phòng thủ để một kênh ném ra cũng không chặn kênh khác.
    private async Task DispatchExternalAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        foreach (var channel in _channels)
        {
            if (!channel.IsEnabled)
                continue;

            try
            {
                await channel.SendAsync(message, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kênh thông báo {Channel} lỗi khi gửi '{Title}'.", channel.Name, message.Title);
            }
        }
    }

    // Ghép BaseUrl (nếu có) với link tương đối để kênh ngoài bấm mở được. Trống ⇒ null (bỏ nút mở).
    private string? ToAbsoluteUrl(string relativeLink)
    {
        var baseUrl = _options.BaseUrl;
        return string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/') + relativeLink;
    }

    // Người nhận đủ điều kiện = user đang hoạt động thuộc role CÓ quyền DeliveryAdvance, kèm tùy chọn thông
    // báo của họ. Bảng user nhỏ (seed vài tài khoản) nên duyệt trực tiếp; kết quả kiểm tra quyền được cache.
    private async Task<IReadOnlyList<EligibleUser>> ResolveEligibleUsersAsync(CancellationToken cancellationToken)
    {
        var activeUsers = await _db.AppUsers
            .Where(u => u.IsActive && u.Username != "")
            .Select(u => new EligibleUser(
                u.Username, u.Role, u.Email,
                u.NotifyInApp, u.NotifyByEmail, u.NotifyOnGate, u.NotifyOnCompleted, u.NotifyOnFailed))
            .ToListAsync(cancellationToken);

        var qualifyingRoles = new HashSet<UserRole>();
        foreach (var role in activeUsers.Select(u => u.Role).Distinct())
        {
            var granted = await _permissions.GetGrantedAsync(role, cancellationToken);
            if (granted.Contains(AppPermission.DeliveryAdvance))
                qualifyingRoles.Add(role);
        }

        return activeUsers.Where(u => qualifyingRoles.Contains(u.Role)).ToList();
    }

    private sealed record EligibleUser(
        string Username,
        UserRole Role,
        string? Email,
        bool NotifyInApp,
        bool NotifyByEmail,
        bool NotifyOnGate,
        bool NotifyOnCompleted,
        bool NotifyOnFailed);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
