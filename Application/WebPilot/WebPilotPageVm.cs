namespace ICOGenerator.Application.WebPilot;

/// <summary>
/// Dữ liệu dựng màn hình thử nghiệm WebPilot. Hai cờ <see cref="AgentReady"/> /
/// <see cref="BrowserReady"/> tồn tại để trang nói thẳng VÌ SAO chưa chạy được, thay vì để người dùng
/// bấm Chạy rồi nhận một lỗi trống.
/// </summary>
/// <param name="Enabled">Cấu hình <c>WebPilot:Enabled</c>.</param>
/// <param name="AgentReady">Đã có agent vai WebPilot trong DB chưa (seed chạy lúc khởi động app).</param>
/// <param name="AgentModelName">Model đang gắn cho vai WebPilot — để người dùng biết lượt chạy tốn tiền ở đâu.</param>
/// <param name="BrowserReady">Lần lái browser gần nhất có khởi động được Chromium không.</param>
/// <param name="BrowserSkipReason">Lý do không lái được (kèm lệnh cài Chromium) khi <paramref name="BrowserReady"/> = false.</param>
/// <param name="Headless">Chạy ẩn hay mở cửa sổ thật — quyết định người dùng có nhìn thấy agent lái không.</param>
/// <param name="ProfileDir">Thư mục hồ sơ Chromium giữ phiên đăng nhập, để người dùng biết đăng nhập tay ở đâu.</param>
/// <param name="MaxSteps">Ngân sách bước mặc định của một lượt chạy.</param>
public record WebPilotPageVm(
    bool Enabled,
    bool AgentReady,
    string? AgentModelName,
    bool BrowserReady,
    string? BrowserSkipReason,
    bool Headless,
    string ProfileDir,
    int MaxSteps);
