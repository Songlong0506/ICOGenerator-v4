namespace ICOGenerator.Services.Notifications;

/// <summary>
/// Cấu hình kênh thông báo NGOÀI (Teams/email) — bind từ section <c>Notifications</c> trong appsettings.
/// Toàn bộ OPT-IN (mặc định TẮT, cùng tinh thần với Llm:Proxy / Otel / StructuredOutput / Budget): chưa cấu
/// hình thì không kênh ngoài nào chạy và không có overhead. Kênh in-app (chuông) KHÔNG phụ thuộc mục này.
/// </summary>
public sealed class NotificationOptions
{
    /// <summary>
    /// URL gốc của app (vd <c>https://icogen.bosch.com</c>) để dựng link tuyệt đối trong thông báo ngoài
    /// (chuông in-app dùng link tương đối). Trống ⇒ thông báo ngoài không kèm nút mở Agent Dashboard.
    /// </summary>
    public string? BaseUrl { get; set; }

    public TeamsChannelOptions Teams { get; set; } = new();

    public EmailChannelOptions Email { get; set; } = new();
}

/// <summary>Teams Incoming Webhook: đăng bài vào một kênh Teams qua URL webhook.</summary>
public sealed class TeamsChannelOptions
{
    public bool Enabled { get; set; }

    /// <summary>URL webhook của kênh Teams (Incoming Webhook / Workflow). Bắt buộc khi <see cref="Enabled"/>.</summary>
    public string? WebhookUrl { get; set; }
}

/// <summary>Email qua SMTP: gửi tới một danh sách người nhận cấu hình sẵn (không theo từng user vì AppUser chưa có email).</summary>
public sealed class EmailChannelOptions
{
    public bool Enabled { get; set; }

    public string? Host { get; set; }
    public int Port { get; set; } = 587;

    /// <summary>Dùng STARTTLS (mặc định) — chuẩn cho cổng 587.</summary>
    public bool UseStartTls { get; set; } = true;

    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Địa chỉ người gửi (From). Bắt buộc khi bật.</summary>
    public string? From { get; set; }

    /// <summary>Danh sách người nhận (To). Bắt buộc ít nhất một khi bật.</summary>
    public string[] To { get; set; } = System.Array.Empty<string>();
}
