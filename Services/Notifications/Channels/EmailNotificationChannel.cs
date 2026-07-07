using System.Net;
using System.Net.Mail;
using System.Text;

namespace ICOGenerator.Services.Notifications.Channels;

/// <summary>
/// Gửi thông báo qua email (SMTP). Người nhận = hợp của danh sách <c>To</c> cố định (admin) và email cá
/// nhân của user đã opt-in (kèm trong <see cref="NotificationMessage.EmailRecipients"/>). OPT-IN: chỉ chạy khi
/// <c>Notifications:Email:Enabled</c> và có Host/From; mỗi lần gửi tự bỏ qua nếu không có người nhận nào.
/// Fail-open: lỗi SMTP chỉ ghi log cảnh báo. Dùng <see cref="SmtpClient"/> của BCL để không thêm phụ thuộc.
/// </summary>
public sealed class EmailNotificationChannel : INotificationChannel
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    private readonly NotificationOptions _options;
    private readonly ILogger<EmailNotificationChannel> _logger;

    public EmailNotificationChannel(NotificationOptions options, ILogger<EmailNotificationChannel> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string Name => "Email";

    // Người nhận có thể đến từ danh sách To cố định HOẶC từ opt-in cá nhân (per-message), nên IsEnabled
    // chỉ xét cấu hình máy chủ; thiếu người nhận cho một thông điệp cụ thể sẽ được bỏ qua trong SendAsync.
    public bool IsEnabled =>
        _options.Email.Enabled
        && !string.IsNullOrWhiteSpace(_options.Email.Host)
        && !string.IsNullOrWhiteSpace(_options.Email.From);

    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return;

        try
        {
            var email = _options.Email;
            using var mail = BuildMail(email, message);
            if (mail.To.Count == 0)
                return; // không có người nhận nào (To trống và chưa ai opt-in) ⇒ bỏ qua.

            using var client = new SmtpClient(email.Host, email.Port)
            {
                EnableSsl = email.UseStartTls,
                Timeout = (int)SendTimeout.TotalMilliseconds
            };
            if (!string.IsNullOrWhiteSpace(email.Username))
                client.Credentials = new NetworkCredential(email.Username, email.Password);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(SendTimeout);
            await client.SendMailAsync(mail, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không gửi được email thông báo '{Title}'.", message.Title);
        }
    }

    // Dựng MailMessage với người nhận = hợp(To cố định, email opt-in trong message), khử trùng lặp không
    // phân biệt hoa thường. Tách để test được mà không cần SMTP thật.
    public static MailMessage BuildMail(EmailChannelOptions email, NotificationMessage message)
    {
        var mail = new MailMessage
        {
            From = new MailAddress(email.From!),
            Subject = string.IsNullOrWhiteSpace(message.ProjectName)
                ? $"[ICOGen] {message.Title}"
                : $"[ICOGen] {message.Title} — {message.ProjectName}",
            Body = BuildBody(message),
            IsBodyHtml = false
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = email.To.Concat(message.EmailRecipients ?? Array.Empty<string>());
        foreach (var addr in candidates)
        {
            if (string.IsNullOrWhiteSpace(addr)) continue;
            var trimmed = addr.Trim();
            if (seen.Add(trimmed))
                mail.To.Add(trimmed);
        }

        return mail;
    }

    private static string BuildBody(NotificationMessage message)
    {
        var body = new StringBuilder();
        body.AppendLine(message.Message);
        if (!string.IsNullOrWhiteSpace(message.ProjectName))
            body.AppendLine().Append("Project: ").AppendLine(message.ProjectName);
        if (!string.IsNullOrWhiteSpace(message.Url))
            body.AppendLine().Append("Mở Agent Dashboard: ").AppendLine(message.Url);
        return body.ToString();
    }
}
