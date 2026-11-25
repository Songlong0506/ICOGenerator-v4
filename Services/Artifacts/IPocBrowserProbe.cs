namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Hỏi tầng kiểm POC: Chromium headless có khởi động được không. Dùng cho nút "Test Environment" ở trang
/// Settings — tách khỏi <see cref="IPocRuntimeChecker"/> để fake của checker trong test không phải hiện thực thêm.
/// <para>
/// Trả lời bằng ĐÚNG browser mà audit POC sẽ dùng (cùng đường tìm, cùng lần tự tải, cùng cache lỗi theo process),
/// nên "Pass" ở đây nghĩa là vòng audit POC kế tiếp chạy được tầng runtime.
/// </para>
/// </summary>
public interface IPocBrowserProbe
{
    Task<PocBrowserProbeResult> ProbeAsync();
}

/// <param name="Available">Browser đã khởi động (hoặc đang chạy sẵn).</param>
/// <param name="Version">Phiên bản Chromium khi khởi động được.</param>
/// <param name="ExecutablePath">Đường dẫn chỉ định qua <c>Poc:RuntimeCheck:BrowserPath</c>/<c>POC_BROWSER_PATH</c>; null = bộ của Playwright.</param>
/// <param name="Failure">Lý do không khởi động được (đã kèm lệnh cài khi là lỗi thiếu binary).</param>
public sealed record PocBrowserProbeResult(bool Available, string? Version, string? ExecutablePath, string? Failure);
