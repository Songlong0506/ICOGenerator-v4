namespace ICOGenerator.Services.Browser;

/// <summary>
/// Cấu hình của WebPilot — vai agent tự lái browser thật (xem <c>docs/agents-and-tools.md</c>).
/// Đọc từ khối <c>WebPilot</c> trong appsettings; mọi giá trị đều có mặc định dùng được ngay.
/// </summary>
public sealed class WebPilotSettings
{
    public const string SectionName = "WebPilot";

    /// <summary>Tắt hẳn đường lái browser: mọi tool web trả về lý do từ chối thay vì mở gì.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Mặc định MỞ cửa sổ (không headless) vì hai lý do của chính tính năng này: hồ sơ trên đĩa giữ
    /// phiên đăng nhập cho các trang NỘI BỘ, và người dùng muốn NHÌN agent lái để thử nghiệm.
    /// </summary>
    public bool Headless { get; set; }

    /// <summary>
    /// Thư mục hồ sơ Chromium (cookie, phiên đăng nhập). Trống ⇒ <c>{AgentWorkspace:RootPath}/.webpilot-profile</c>.
    /// Hồ sơ nằm ngoài lượt chạy nên đăng nhập một lần là các lượt sau dùng lại được.
    /// </summary>
    public string UserDataDir { get; set; } = string.Empty;

    /// <summary>Đường dẫn browser chỉ định sẵn. Trống ⇒ dùng bộ Playwright của máy (tự tải nếu chưa có).</summary>
    public string BrowserPath { get; set; } = string.Empty;

    /// <summary>Tự tải Chromium khi máy chưa có — cùng tinh thần với <c>Poc:RuntimeCheck:AutoInstall</c>.</summary>
    public bool AutoInstall { get; set; } = true;

    public int NavigationTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Trần ký tự text trả về cho model mỗi lần chụp trạng thái trang. Một trang kết quả tìm kiếm để
    /// nguyên là đủ đốt sạch ngân sách bước của lượt chạy.
    /// </summary>
    public int MaxTextChars { get; set; } = 3000;

    /// <summary>Trần số điều khiển trong bản đồ đánh số gửi cho model.</summary>
    public int MaxControls { get; set; } = 40;

    /// <summary>Ngân sách bước mặc định của một lượt chạy thử trên trang WebPilot.</summary>
    public int MaxSteps { get; set; } = 20;
}
