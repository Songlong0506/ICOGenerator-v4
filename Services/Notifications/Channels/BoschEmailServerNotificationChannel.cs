using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ICOGenerator.Services.Notifications.Channels;

/// <summary>
/// Gửi thông báo qua <b>Email Server API của Bosch</b> — HTTP endpoint nội bộ dùng chung bởi nhiều app Bosch
/// (tham chiếu <c>SendMailService</c> gốc). Thay cho SMTP trực tiếp khi hạ tầng chỉ mở API: POST một mảng JSON
/// <c>[{ To, From, Subject, Body, Cc, Bcc }]</c> tới <c>{BaseUrl}/{SendMailApi}</c> kèm header <c>ApiKey</c>.
///
/// Người nhận = hợp của danh sách <c>To</c> cố định và email cá nhân của user đã opt-in
/// (<see cref="NotificationMessage.EmailRecipients"/>). OPT-IN: chỉ chạy khi
/// <c>Notifications:BoschEmail:Enabled</c> và đủ BaseUrl/ApiKey/FromEmail. Fail-open: lỗi mạng/HTTP chỉ ghi log
/// cảnh báo, không ném ra ngoài. Giữ nguyên CHỐT AN TOÀN <c>OnlySendToTesterEmail</c> của bản gốc để non-prod
/// không lỡ gửi ra người nhận thật.
/// </summary>
public sealed class BoschEmailServerNotificationChannel : INotificationChannel
{
    // Email Server đôi khi phản hồi chậm — chốt trần thời gian để không giữ chân worker khi cổng mở.
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly NotificationOptions _options;
    private readonly ILogger<BoschEmailServerNotificationChannel> _logger;

    public BoschEmailServerNotificationChannel(HttpClient http, NotificationOptions options, ILogger<BoschEmailServerNotificationChannel> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public string Name => "BoschEmail";

    public bool IsEnabled =>
        _options.BoschEmail.Enabled
        && !string.IsNullOrWhiteSpace(_options.BoschEmail.BaseUrl)
        && !string.IsNullOrWhiteSpace(_options.BoschEmail.ApiKey)
        && !string.IsNullOrWhiteSpace(_options.BoschEmail.FromEmail);

    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return;

        try
        {
            var email = _options.BoschEmail;
            var request = BuildRequest(email, message);
            if (request.To.Count == 0)
                return; // không có người nhận nào (To trống và chưa ai opt-in) ⇒ bỏ qua.

            // API nhận một MẢNG email (bản gốc serialize List<SendMailRequestDTO>); gửi một phần tử.
            var payload = JsonSerializer.Serialize(new[] { request }, JsonOptions);

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var http = new HttpRequestMessage(HttpMethod.Post, BuildUri(email))
            {
                Content = content
            };
            http.Headers.Add("ApiKey", email.ApiKey);
            http.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(SendTimeout);
            using var response = await _http.SendAsync(http, cts.Token);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Email Server trả {StatusCode} khi gửi thông báo '{Title}'.", (int)response.StatusCode, message.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không gửi được email (Email Server) '{Title}'.", message.Title);
        }
    }

    // Ghép URL tuyệt đối tới endpoint gửi mail; chuẩn hóa dấu '/' thừa ở hai đầu.
    public static Uri BuildUri(EmailServerChannelOptions email)
    {
        var baseUrl = (email.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        var path = (email.SendMailApi ?? string.Empty).Trim().TrimStart('/');
        return new Uri($"{baseUrl}/{path}");
    }

    // Dựng request theo hợp đồng API (To/From/Subject/Body). Người nhận = hợp(To cố định, email opt-in), khử
    // trùng lặp không phân biệt hoa thường; nếu OnlySendToTesterEmail thì lọc về danh sách tester (CHỐT AN
    // TOÀN). Tách static để test được mà không cần mạng.
    public static SendMailRequest BuildRequest(EmailServerChannelOptions email, NotificationMessage message)
    {
        var recipients = MergeRecipients(email.To, message.EmailRecipients);

        if (email.OnlySendToTesterEmail)
            recipients = ApplyTesterFilter(recipients, email.TesterEmail);

        return new SendMailRequest
        {
            To = recipients,
            From = email.FromEmail!,
            Subject = BuildSubject(message),
            Body = BuildBody(message)
        };
    }

    // Hợp hai nguồn người nhận, khử trùng không phân biệt hoa/thường, giữ thứ tự xuất hiện.
    private static List<string> MergeRecipients(IEnumerable<string>? configured, IEnumerable<string>? perUser)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var addr in (configured ?? Array.Empty<string>()).Concat(perUser ?? Array.Empty<string>()))
        {
            if (string.IsNullOrWhiteSpace(addr)) continue;
            var trimmed = addr.Trim();
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }
        return result;
    }

    // CHỐT AN TOÀN: chỉ giữ người nhận nằm trong danh sách tester; nếu không còn ai thì gửi tới toàn bộ tester
    // (đúng hành vi bản gốc: item.To.AddRange(TesterEmail) khi rỗng).
    public static List<string> ApplyTesterFilter(List<string> recipients, IReadOnlyList<string>? testerEmails)
    {
        var testers = testerEmails ?? Array.Empty<string>();
        var filtered = recipients
            .Where(r => testers.Any(t => string.Equals(t, r, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return filtered.Count > 0
            ? filtered
            : testers.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
    }

    private static string BuildSubject(NotificationMessage message) =>
        string.IsNullOrWhiteSpace(message.ProjectName)
            ? $"[ICOGen] {message.Title}"
            : $"[ICOGen] {message.Title} — {message.ProjectName}";

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

/// <summary>
/// Payload gửi tới Email Server API (khớp <c>SendMailRequestDTO</c> phía server). Chỉ dùng các trường cần cho
/// thông báo; <c>Cc</c>/<c>Bcc</c> để dành khi cần mở rộng.
/// </summary>
public sealed class SendMailRequest
{
    public List<string> To { get; set; } = new();
    public string From { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public List<string>? Cc { get; set; }
    public List<string>? Bcc { get; set; }
}
