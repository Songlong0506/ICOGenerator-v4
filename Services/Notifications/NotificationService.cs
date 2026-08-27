using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Notifications;

/// <summary>
/// Hiện thực <see cref="INotificationService"/>: xác định người nhận rồi <c>Add</c> một
/// <see cref="Notification"/> cho mỗi người vào DbContext hiện hành. Không SaveChanges (xem hợp đồng ở
/// interface). Toàn bộ bọc try/catch fail-open.
/// <para>
/// <b>ĐANG TẮT TẠM THỜI — xem <see cref="Enabled"/>.</b> Bốn đường vào đều trả về ngay: không dòng
/// <c>Notifications</c> nào được ghi, không kênh ngoài nào được gọi. Bộ máy bên dưới giữ nguyên để bật lại
/// chỉ là đổi một hằng số.
/// </para>
/// </summary>
public class NotificationService : INotificationService
{
    /// <summary>
    /// Công tắc TẠM THỜI của toàn bộ việc gửi thông báo. Đặt <c>false</c> vì cách chọn người nhận cũ đã hỏng:
    /// nó lọc theo quyền <see cref="AppPermission.DeliveryAdvance"/>, mà quyền suy ra từ vai trò, còn vai trò
    /// nay chỉ tồn tại trong claim của phiên đăng nhập (xem <see cref="Domain.AppUser"/>) — người cần được
    /// báo thì đang OFFLINE, không có phiên nào để đọc. Cách duy nhất còn lại là gửi cho MỌI user, tức là
    /// làm phiền cả những người không có quyền duyệt cổng, nên thà im còn hơn.
    /// <para>
    /// Bật lại: đổi thành <c>true</c> SAU KHI đã có tiêu chí chọn người nhận không phụ thuộc vai trò — ví dụ
    /// một bảng đăng ký người theo dõi từng project, hoặc cột người phụ trách trên <c>Project</c>. Chỉ đổi
    /// hằng số này mà không sửa <see cref="ResolveRecipientsAsync"/> thì mọi user sẽ nhận mọi thông báo.
    /// </para>
    /// </summary>
    private const bool Enabled = false;

    private readonly AppDbContext _db;
    private readonly IEnumerable<INotificationChannel> _channels;
    private readonly NotificationOptions _options;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        AppDbContext db,
        IEnumerable<INotificationChannel> channels,
        NotificationOptions options,
        ILogger<NotificationService> logger)
    {
        _db = db;
        _channels = channels;
        _options = options;
        _logger = logger;
    }

    public Task NotifyGateOpenedAsync(WorkflowRun run, string nextStepTitle, CancellationToken cancellationToken = default) =>
        Enabled
            ? CreateForEligibleAsync(run, NotificationType.GateAwaitingApproval,
                "Chờ duyệt bước delivery",
                $"Một bước đã xong — chờ bạn duyệt để sang: {nextStepTitle}.",
                cancellationToken)
            : Task.CompletedTask;

    public Task NotifyRunCompletedAsync(WorkflowRun run, CancellationToken cancellationToken = default) =>
        Enabled
            ? CreateForEligibleAsync(run, NotificationType.WorkflowCompleted,
                "Workflow hoàn tất",
                "Quy trình giao hàng đã chạy xong tất cả các bước.",
                cancellationToken)
            : Task.CompletedTask;

    public Task NotifyRunFailedAsync(WorkflowRun run, string? error, CancellationToken cancellationToken = default) =>
        Enabled
            ? CreateForEligibleAsync(run, NotificationType.WorkflowFailed,
                "Workflow thất bại",
                string.IsNullOrWhiteSpace(error) ? "Quy trình giao hàng đã dừng vì lỗi — cần xem lại." : $"Quy trình dừng vì lỗi: {Truncate(error, 300)}",
                cancellationToken)
            : Task.CompletedTask;

    public Task NotifyPocAcceptedAsync(WorkflowRun run, string acceptedBy, CancellationToken cancellationToken = default) =>
        Enabled
            ? CreateForEligibleAsync(run, NotificationType.PocAccepted,
                "Bản demo đã được nghiệm thu",
                $"{acceptedBy} xác nhận bản demo (POC) đã đạt — có thể duyệt cổng POC để đi tiếp các bước sau.",
                cancellationToken)
            : Task.CompletedTask;

    public Task NotifyPocAcceptanceWithdrawnAsync(WorkflowRun run, string withdrawnBy, CancellationToken cancellationToken = default) =>
        Enabled
            ? CreateForEligibleAsync(run, NotificationType.PocAcceptanceWithdrawn,
                "Nghiệm thu bản demo đã bị rút",
                $"{withdrawnBy} rút lại lời nghiệm thu bản demo (POC) — đang có góp ý cần xử lý, đừng duyệt cổng POC cho tới khi được nghiệm thu lại.",
                cancellationToken)
            : Task.CompletedTask;

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

            var eligible = await ResolveRecipientsAsync(cancellationToken);

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
    private static bool WantsType(Recipient user, NotificationType type) => type switch
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

    // Người nhận = mọi user có username, kèm tùy chọn thông báo của họ (việc lọc theo tùy chọn nằm ở bên
    // gọi). CHÍNH CHỖ NÀY là lý do Enabled đang false: không còn cách nào lọc ra "ai nên được báo", nên bật
    // lại mà không thay tiêu chí ở đây thì mọi user sẽ nhận mọi thông báo. Bảng user nhỏ (seed vài tài
    // khoản, cộng các user SSO tự tạo) nên nạp thẳng một lượt.
    private async Task<IReadOnlyList<Recipient>> ResolveRecipientsAsync(CancellationToken cancellationToken) =>
        await _db.AppUsers
            .Where(u => u.Username != "")
            .Select(u => new Recipient(
                u.Username, u.Email,
                u.NotifyInApp, u.NotifyByEmail, u.NotifyOnGate, u.NotifyOnCompleted, u.NotifyOnFailed))
            .ToListAsync(cancellationToken);

    private sealed record Recipient(
        string Username,
        string? Email,
        bool NotifyInApp,
        bool NotifyByEmail,
        bool NotifyOnGate,
        bool NotifyOnCompleted,
        bool NotifyOnFailed);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
