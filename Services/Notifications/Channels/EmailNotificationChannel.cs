using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ICOGenerator.Services.Notifications.Channels;

/// <summary>
/// Gửi thông báo qua email (SMTP). Người nhận = hợp của danh sách <c>To</c> cố định (admin) và email cá
/// nhân của user đã opt-in (kèm trong <see cref="NotificationMessage.EmailRecipients"/>). OPT-IN: chỉ chạy khi
/// <c>Notifications:Email:Enabled</c> và có Host/From; mỗi lần gửi tự bỏ qua nếu không có người nhận nào.
/// Fail-open: lỗi SMTP chỉ ghi log cảnh báo.
/// <para>
/// Dùng MailKit chứ không phải <c>System.Net.Mail.SmtpClient</c> của BCL. Lý do cụ thể, không phải
/// "cho hiện đại": <c>SmtpClient.EnableSsl</c> KHÔNG phải là công tắc STARTTLS — nó là một cờ mà lớp đó
/// tự diễn giải, và nó <b>không hỗ trợ implicit TLS (cổng 465)</b> ở bất kỳ cấu hình nào. Code cũ gán
/// thẳng <c>EnableSsl = UseStartTls</c>, nên ai đặt <c>Port: 465</c> — một cấu hình SMTP rất phổ biến —
/// sẽ nhận một lỗi kết nối không giải thích được; mà kênh này fail-open nên thông báo lặng lẽ không bao
/// giờ tới. MailKit tách bạch ba chế độ qua <see cref="SecureSocketOptions"/>. Nó cũng là thứ Microsoft
/// khuyến nghị thay cho <c>SmtpClient</c> ở code mới, và là đường duy nhất tới OAuth2/XOAUTH2 nếu SMTP
/// nội bộ sau này bật modern auth.
/// </para>
/// </summary>
public sealed class EmailNotificationChannel : INotificationChannel
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Cổng SMTP quy ước cho implicit TLS (TLS ngay từ lúc mở kết nối, không qua lệnh STARTTLS).</summary>
    private const int ImplicitTlsPort = 465;

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

    /// <summary>
    /// Chế độ bảo mật của kết nối, suy từ cấu hình. Tách ra để test được mà không cần máy chủ SMTP thật —
    /// đây đúng là chỗ bản cũ sai, nên nó phải có test riêng chứ không nằm lẫn trong đường gửi.
    /// </summary>
    public static SecureSocketOptions SecurityFor(EmailChannelOptions email) =>
        !email.UseStartTls ? SecureSocketOptions.None
        : email.Port == ImplicitTlsPort ? SecureSocketOptions.SslOnConnect
        : SecureSocketOptions.StartTls;

    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return;

        try
        {
            var email = _options.Email;
            var mail = BuildMail(email, message);
            if (mail.To.Count == 0)
                return; // không có người nhận nào (To trống và chưa ai opt-in) ⇒ bỏ qua.

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(SendTimeout);

            using var client = new SmtpClient { Timeout = (int)SendTimeout.TotalMilliseconds };
            await client.ConnectAsync(email.Host, email.Port, SecurityFor(email), cts.Token);

            if (!string.IsNullOrWhiteSpace(email.Username))
                await client.AuthenticateAsync(email.Username, email.Password, cts.Token);

            await client.SendAsync(mail, cts.Token);
            await client.DisconnectAsync(quit: true, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không gửi được email thông báo '{Title}'.", message.Title);
        }
    }

    // Dựng MimeMessage với người nhận = hợp(To cố định, email opt-in trong message), khử trùng lặp không
    // phân biệt hoa thường. Tách để test được mà không cần SMTP thật.
    public static MimeMessage BuildMail(EmailChannelOptions email, NotificationMessage message)
    {
        var mail = new MimeMessage
        {
            Subject = EmailNotificationText.Subject(message),
            Body = new TextPart("plain") { Text = EmailNotificationText.Body(message) }
        };
        mail.From.Add(MailboxAddress.Parse(email.From!));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = email.To.Concat(message.EmailRecipients ?? Array.Empty<string>());
        foreach (var addr in candidates)
        {
            if (string.IsNullOrWhiteSpace(addr)) continue;
            var trimmed = addr.Trim();
            if (seen.Add(trimmed))
                mail.To.Add(MailboxAddress.Parse(trimmed));
        }

        return mail;
    }
}
