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

    /// <summary>
    /// Email qua Email Server API nội bộ của Bosch (HTTP, giống các app khác trong Bosch đang dùng) — thay
    /// cho SMTP trực tiếp khi hạ tầng chỉ mở API. Xem <see cref="EmailServerChannelOptions"/>.
    /// </summary>
    public EmailServerChannelOptions BoschEmail { get; set; } = new();
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

/// <summary>
/// Email qua <b>Email Server API của Bosch</b> — HTTP endpoint nội bộ mà nhiều app Bosch dùng chung để gửi
/// mail (thay cho việc mỗi app tự cấu hình SMTP). Gửi bằng cách POST một mảng JSON tới
/// <c>{BaseUrl}/{SendMailApi}</c> kèm header <c>ApiKey</c>. Tất cả OPT-IN như các kênh khác; self-disable khi
/// thiếu <see cref="BaseUrl"/>/<see cref="ApiKey"/>/<see cref="FromEmail"/>. Nạp <see cref="ApiKey"/> qua biến
/// môi trường <c>Notifications__BoschEmail__ApiKey</c> (KHÔNG commit).
/// </summary>
public sealed class EmailServerChannelOptions
{
    public bool Enabled { get; set; }

    /// <summary>URL gốc của Email Server (vd <c>https://lthvmapp01.apac.bosch.com/email-server-api</c>). Bắt buộc khi bật.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Đường dẫn endpoint gửi mail, ghép sau <see cref="BaseUrl"/>. Mặc định theo hợp đồng API hiện tại.</summary>
    public string SendMailApi { get; set; } = "api/Email";

    /// <summary>Khóa API gửi qua header <c>ApiKey</c>. Bắt buộc khi bật; nạp qua biến môi trường, không commit.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Địa chỉ người gửi (From). Bắt buộc khi bật (vd <c>auto-mail-no-reply@vn.bosch.com</c>).</summary>
    public string? FromEmail { get; set; }

    /// <summary>Danh sách người nhận cố định (To), gộp với email cá nhân đã opt-in trong từng thông điệp.</summary>
    public string[] To { get; set; } = System.Array.Empty<string>();

    /// <summary>
    /// CHỐT AN TOÀN cho môi trường non-prod: khi bật, mọi người nhận bị lọc chỉ còn <see cref="TesterEmail"/>
    /// (khử người nhận thật để không lỡ gửi ra ngoài). Nếu sau khi lọc không còn ai, gửi tới toàn bộ tester.
    /// </summary>
    public bool OnlySendToTesterEmail { get; set; }

    /// <summary>Danh sách email tester được phép nhận khi <see cref="OnlySendToTesterEmail"/> bật.</summary>
    public string[] TesterEmail { get; set; } = System.Array.Empty<string>();
}
