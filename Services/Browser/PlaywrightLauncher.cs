using Microsoft.Playwright;

namespace ICOGenerator.Services.Browser;

/// <summary>
/// Chỗ DUY NHẤT lo việc "có Chromium để lái hay không": tạo <see cref="IPlaywright"/> một lần cho cả
/// process, tìm binary, và TỰ TẢI bộ browser lần đầu nếu máy chưa có.
///
/// <para>
/// Tách ra khỏi <see cref="Artifacts.PlaywrightPocRuntimeChecker"/> vì nay có HAI hộ dùng browser với
/// nhu cầu khác hẳn nhau: tầng kiểm POC cần một <c>IBrowser</c> headless dùng chung, còn WebPilot cần
/// một <i>persistent context</i> có hồ sơ riêng trên đĩa (để đăng nhập một lần là dùng được cho các
/// lượt sau) — hai thứ không chia nhau một <c>IBrowser</c> được. Phần chia được, và là phần tinh tế
/// nhất, chính là đoạn dưới đây: chép nó lần thứ hai là chép luôn mọi cạm bẫy của nó.
/// </para>
///
/// <para>
/// FAIL-OPEN: mọi lỗi đều trả null kèm lý do đọc được (<see cref="LastFailure"/>) thay vì ném — hộ
/// dùng tự quyết định bỏ qua hay báo lên UI. Lỗi launch được NHỚ LẠI để không tốn thời gian thử lại ở
/// mỗi vòng.
/// </para>
/// </summary>
public sealed class PlaywrightLauncher : IAsyncDisposable
{
    /// <summary>
    /// Nhắc đúng lệnh cài trong log VÀ trong lý do skip hiện trên UI: người gặp dòng "không khởi động
    /// được Chromium headless" biết ngay phải gõ gì, khỏi đi tra tài liệu.
    /// </summary>
    public const string InstallHint = "pwsh bin/Debug/net8.0/playwright.ps1 install chromium";

    private readonly ILogger<PlaywrightLauncher> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private IPlaywright? _playwright;
    private bool _installAttempted;

    public PlaywrightLauncher(ILogger<PlaywrightLauncher> logger)
    {
        _logger = logger;
    }

    /// <summary>Lý do lần khởi động gần nhất thất bại (null = chưa từng hỏng).</summary>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// Khởi động một thứ gì đó bằng Playwright (<paramref name="launch"/> nhận <see cref="IPlaywright"/>
    /// và trả về browser/context tuỳ hộ dùng). Nếu lỗi đúng dạng "chưa có binary" thì tải bộ Chromium
    /// rồi thử LẠI đúng một lần.
    /// </summary>
    /// <param name="launch">Hàm launch của hộ dùng — chạy tối đa hai lần (lần hai sau khi tải browser).</param>
    /// <param name="explicitExecutablePath">
    /// Đường dẫn browser do cấu hình chỉ định, hoặc null/rỗng khi dùng bộ Playwright của máy. Đã chỉ
    /// đường mà sai thì tải về cũng không dùng tới, nên có giá trị này là KHÔNG tự tải.
    /// </param>
    /// <param name="autoInstall">Bật/tắt đường tự tải (mỗi hộ dùng có key cấu hình riêng).</param>
    /// <param name="autoInstallTimeoutSeconds">Trần chờ lượt tải browser.</param>
    public async Task<T?> LaunchAsync<T>(
        Func<IPlaywright, Task<T>> launch,
        string? explicitExecutablePath,
        bool autoInstall,
        int autoInstallTimeoutSeconds = 300) where T : class
    {
        await _lock.WaitAsync();
        try
        {
            _playwright ??= await Playwright.CreateAsync();

            try
            {
                var result = await launch(_playwright);
                LastFailure = null;
                return result;
            }
            catch (Exception ex) when (ShouldAttemptInstall(explicitExecutablePath, ex.Message, autoInstall))
            {
                // Máy mới clone source về: package NuGet có sẵn nhưng binary Chromium thì chưa (nằm ở
                // cache dùng chung của máy, không nằm trong repo). Tải một lần rồi thử lại thay vì bắt
                // người dùng đọc lý do skip trên UI mới biết mình phải cài gì.
                if (!await TryInstallChromiumAsync(autoInstallTimeoutSeconds))
                    throw;
                var result = await launch(_playwright);
                LastFailure = null;
                return result;
            }
        }
        catch (Exception ex)
        {
            LastFailure = FirstLine(ex.Message);
            if (IsMissingBrowserError(ex.Message))
                LastFailure += $" — cài bằng: {InstallHint}";
            _logger.LogWarning(ex, "Không khởi động được Chromium — hộ dùng sẽ tự bỏ qua.");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Có nên tự tải bộ browser rồi thử lại không. Chỉ đúng khi CẢ BA điều kiện cùng đúng: bật cấu hình,
    /// KHÔNG có đường dẫn browser chỉ định sẵn (đã chỉ đường mà sai thì tải về cũng không dùng tới), và
    /// lỗi launch đúng dạng "chưa có binary". Lỗi khác — thiếu thư viện hệ điều hành, sandbox chặn — tải
    /// 150MB về cũng không chữa được, nên fail-open ngay.
    /// </summary>
    internal static bool ShouldAttemptInstall(string? explicitExecutablePath, string launchError, bool autoInstallEnabled)
        => autoInstallEnabled
           && string.IsNullOrWhiteSpace(explicitExecutablePath)
           && IsMissingBrowserError(launchError);

    internal static bool IsMissingBrowserError(string message)
        => message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
           || message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tải bộ Chromium của Playwright (tương đương <c>playwright.ps1 install chromium</c>) vào cache dùng
    /// chung của máy. Chỉ thử MỘT lần mỗi process: hết hạn chờ hoặc lỗi mạng thì bỏ qua — không bao giờ
    /// chặn việc gì chỉ vì tải browser hỏng.
    /// </summary>
    private async Task<bool> TryInstallChromiumAsync(int timeoutSeconds)
    {
        if (_installAttempted)
            return false;
        _installAttempted = true;

        var timeout = TimeSpan.FromSeconds(Math.Max(30, timeoutSeconds));
        _logger.LogInformation(
            "Chưa có Chromium — đang tải bộ browser Playwright (một lần cho mỗi máy, ~150MB, tối đa {Timeout}).",
            timeout);

        try
        {
            // Program.Main là API cài đặt chính thức của Microsoft.Playwright; nó chạy đồng bộ nên đẩy
            // sang thread pool để không chặn caller. Quá hạn thì bỏ mặc lượt tải chạy nốt dưới nền — lần
            // khởi động app sau sẽ thấy binary đã có sẵn.
            var install = Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
            if (await Task.WhenAny(install, Task.Delay(timeout)) != install)
            {
                _logger.LogWarning("Tải Chromium quá {Timeout} — bỏ qua vòng này.", timeout);
                return false;
            }

            var exitCode = await install;
            if (exitCode != 0)
            {
                _logger.LogWarning("Lệnh tải Chromium trả về mã lỗi {ExitCode}. Cài thủ công bằng: {Hint}", exitCode, InstallHint);
                return false;
            }

            _logger.LogInformation("Đã tải xong Chromium — thử khởi động lại.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không tải được Chromium. Cài thủ công bằng: {Hint}", InstallHint);
            return false;
        }
    }

    internal static string FirstLine(string text)
    {
        var idx = text.IndexOf('\n');
        return (idx < 0 ? text : text[..idx]).Trim();
    }

    public ValueTask DisposeAsync()
    {
        _playwright?.Dispose();
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}
