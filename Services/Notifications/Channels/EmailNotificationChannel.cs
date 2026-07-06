using System.Net;
using System.Net.Mail;
using System.Text;

namespace ICOGenerator.Services.Notifications.Channels;

/// <summary>
/// Gửi thông báo qua email (SMTP) tới danh sách người nhận cấu hình sẵn. OPT-IN: chỉ chạy khi
/// <c>Notifications:Email:Enabled</c> và có đủ Host/From/To. Fail-open: lỗi SMTP chỉ ghi log cảnh báo.
/// Dùng <see cref="SmtpClient"/> có sẵn trong BCL để không thêm phụ thuộc; đủ cho SMTP nội bộ + STARTTLS 587.
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

    public bool IsEnabled =>
        _options.Email.Enabled
        && !string.IsNullOrWhiteSpace(_options.Email.Host)
        && !string.IsNullOrWhiteSpace(_options.Email.From)
        && _options.Email.To.Any(a => !string.IsNullOrWhiteSpace(a));

    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return;

        try
        {
            var email = _options.Email;
            using var mail = BuildMail(email, message);
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

    // Dựng MailMessage. Tách để test được mà không cần SMTP thật.
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

        foreach (var to in email.To.Where(a => !string.IsNullOrWhiteSpace(a)))
            mail.To.Add(to.Trim());

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
