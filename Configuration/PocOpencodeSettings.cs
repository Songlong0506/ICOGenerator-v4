namespace ICOGenerator.Configuration;

/// <summary>
/// Vỏ agent opencode cho lượt "Thử model khác" ở bước POC (section <c>"PocOpencode"</c>) — xem
/// <c>Services/Workflows/PocOpencodeBuilder</c> và docs/workspace-and-poc.md. Đọc một lần lúc khởi động:
/// đổi cấu hình cần restart app.
/// </summary>
public sealed class PocOpencodeSettings
{
    public const string SectionName = "PocOpencode";

    /// <summary>
    /// Bật lựa chọn vỏ opencode trong hộp thoại "Thử model khác". Mặc định TẮT: nó cần opencode được cài
    /// trên máy chủ app và cho một tiến trình ngoài app chạy lệnh (giới hạn ở <c>node</c>) trong thư mục thử.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Lệnh chạy opencode: tên trên PATH hoặc đường dẫn đầy đủ. Trên Windows bản cài qua npm là
    /// <c>opencode.cmd</c> — ghi rõ phần mở rộng (hoặc đường dẫn tới <c>opencode.exe</c>).
    /// </summary>
    public string Command { get; set; } = "opencode";

    /// <summary>
    /// Địa chỉ gốc mà opencode dùng để gọi ngược relay của app (vd <c>http://127.0.0.1:5000</c>). Bỏ trống
    /// ⇒ lấy địa chỉ <c>http://</c> đầu tiên app đang nghe (Kestrel). Phải đặt khi app chạy sau IIS
    /// in-process hoặc chỉ nghe HTTPS bằng chứng chỉ máy dev.
    /// </summary>
    public string? RelayBaseUrl { get; set; }

    /// <summary>Trần thời gian của MỘT lần chạy opencode (lượt dựng hoặc một lượt sửa). Quá trần ⇒ dừng cả cây tiến trình.</summary>
    public int RunTimeoutMinutes { get; set; } = 40;
}
