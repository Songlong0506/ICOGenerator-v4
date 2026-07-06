using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Notifications.Channels;

/// <summary>
/// Đẩy thông báo tới một kênh Teams qua Incoming Webhook (định dạng MessageCard — được cả connector cũ lẫn
/// workflow mới chấp nhận). OPT-IN: chỉ chạy khi <c>Notifications:Teams:Enabled</c> và có WebhookUrl.
/// Fail-open: lỗi mạng/HTTP chỉ ghi log cảnh báo, không ném ra ngoài.
/// </summary>
public sealed class TeamsNotificationChannel : INotificationChannel
{
    // Teams webhook đôi khi treo lâu — chốt trần thời gian để không giữ chân worker khi cổng mở.
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly NotificationOptions _options;
    private readonly ILogger<TeamsNotificationChannel> _logger;

    public TeamsNotificationChannel(HttpClient http, NotificationOptions options, ILogger<TeamsNotificationChannel> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public string Name => "Teams";

    public bool IsEnabled => _options.Teams.Enabled && !string.IsNullOrWhiteSpace(_options.Teams.WebhookUrl);

    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(SendTimeout);

            var payload = BuildMessageCard(message);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_options.Teams.WebhookUrl, content, cts.Token);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Teams webhook trả {StatusCode} khi gửi thông báo '{Title}'.", (int)response.StatusCode, message.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không gửi được thông báo Teams '{Title}'.", message.Title);
        }
    }

    // Dựng payload MessageCard. Tách static để test được mà không cần mạng.
    public static string BuildMessageCard(NotificationMessage message)
    {
        var text = new StringBuilder(message.Message);
        if (!string.IsNullOrWhiteSpace(message.ProjectName))
            text.Append("\n\n**Project:** ").Append(message.ProjectName);

        var card = new JsonObject
        {
            ["@type"] = "MessageCard",
            ["@context"] = "https://schema.org/extensions",
            ["themeColor"] = ThemeColor(message.Type),
            ["summary"] = message.Title,
            ["title"] = message.Title,
            ["text"] = text.ToString()
        };

        if (!string.IsNullOrWhiteSpace(message.Url))
        {
            card["potentialAction"] = new JsonArray
            {
                new JsonObject
                {
                    ["@type"] = "OpenUri",
                    ["name"] = "Mở Agent Dashboard",
                    ["targets"] = new JsonArray
                    {
                        new JsonObject { ["os"] = "default", ["uri"] = message.Url }
                    }
                }
            };
        }

        return card.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    // Màu viền card theo loại (mã hex Bosch, không kèm '#').
    private static string ThemeColor(NotificationType type) => type switch
    {
        NotificationType.WorkflowCompleted => "00884A", // xanh lá
        NotificationType.WorkflowFailed => "E20015",    // đỏ
        _ => "F58220"                                    // cam — chờ duyệt
    };
}
