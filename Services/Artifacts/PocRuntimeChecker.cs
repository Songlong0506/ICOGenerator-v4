using Microsoft.Playwright;

namespace ICOGenerator.Services.Artifacts;

/// <summary>Ảnh chụp một màn hình POC (PNG) để tầng Visual QA (vision model) chấm bố cục/dữ liệu mẫu.</summary>
public sealed record PocScreenshot(string Screen, byte[] Png);

/// <summary>Kết quả POC tự tính một worked example (window.pocWorkedExamples): ref + giá trị POC tính ra.</summary>
public sealed record PocWorkedExampleResult(string Ref, string Computed);

/// <summary>Kết quả một lần kiểm tra runtime POC. <see cref="Ran"/> = false nghĩa là bị bỏ qua (không có browser/tắt cấu hình) — audit vẫn tiếp tục với phần tĩnh.</summary>
public sealed record PocRuntimeReport(
    bool Ran,
    string? SkipReason,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> SelfTestResults,
    IReadOnlyList<PocScreenshot> Screenshots,
    IReadOnlyList<PocWorkedExampleResult> WorkedExampleResults)
{
    public static PocRuntimeReport Skipped(string reason) =>
        new(false, reason, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<PocScreenshot>(), Array.Empty<PocWorkedExampleResult>());
}

/// <summary>
/// Kiểm tra RUNTIME của poc-demo.html — tầng mà <see cref="PocAudit"/> (scan chuỗi) không với tới:
/// một TypeError trong script nghiệp vụ làm chết toàn bộ tương tác nhưng audit tĩnh vẫn "OK".
/// Mở file trong Chromium headless, đi qua TỪNG màn hình bằng chính pocNavigate của shell, gom lỗi
/// JS (pageerror + console error), và chạy window.pocSelfTest() — bộ assertion mỗi Business Rule mà
/// prompt POC yêu cầu agent tự sinh — để rule fail thành ISSUE cụ thể thay vì lời tự khai.
/// <para>
/// <paramref name="captureScreenshots"/> = true còn chụp ảnh TỪNG màn hình để tầng Visual QA (vision
/// model) chấm bố cục/dữ liệu mẫu — thứ mà cả scan chuỗi lẫn self-test đều "mù" (màn hình trống trơn,
/// layout vỡ vẫn pass). Chỉ bật khi có agent UI/UX vision để không chụp phí.
/// </para>
/// </summary>
public interface IPocRuntimeChecker
{
    Task<PocRuntimeReport> CheckAsync(string pocHtmlPath, bool captureScreenshots = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Hiện thực bằng Microsoft.Playwright. FAIL-OPEN toàn phần: môi trường không có Chromium (hoặc tắt qua
/// <c>Poc:RuntimeCheck:Enabled</c>) thì trả Skipped kèm lý do — audit POC vẫn chạy phần tĩnh như cũ,
/// không bao giờ chặn pipeline vì thiếu browser. Đường tìm browser: cấu hình
/// <c>Poc:RuntimeCheck:BrowserPath</c> → biến môi trường <c>POC_BROWSER_PATH</c> → bộ browser Playwright
/// đã cài (PLAYWRIGHT_BROWSERS_PATH). Browser được giữ lại dùng chung (singleton) vì audit chạy tới 3
/// vòng mỗi POC — mỗi lần launch lại tốn ~nửa giây.
/// </summary>
public sealed class PlaywrightPocRuntimeChecker : IPocRuntimeChecker, IAsyncDisposable
{
    private const int MaxScreens = 20;
    private const int MaxIssues = 20;
    private const float GotoTimeoutMs = 15000;

    private readonly IConfiguration _configuration;
    private readonly ILogger<PlaywrightPocRuntimeChecker> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private string? _launchFailure;

    public PlaywrightPocRuntimeChecker(IConfiguration configuration, ILogger<PlaywrightPocRuntimeChecker> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PocRuntimeReport> CheckAsync(string pocHtmlPath, bool captureScreenshots = false, CancellationToken cancellationToken = default)
    {
        if (!_configuration.GetValue("Poc:RuntimeCheck:Enabled", true))
            return PocRuntimeReport.Skipped("tắt qua cấu hình Poc:RuntimeCheck:Enabled.");

        if (!File.Exists(pocHtmlPath))
            return PocRuntimeReport.Skipped($"không tìm thấy file: {pocHtmlPath}");

        var browser = await GetBrowserAsync();
        if (browser == null)
            return PocRuntimeReport.Skipped($"không khởi động được Chromium headless ({_launchFailure}).");

        try
        {
            return await RunCheckAsync(browser, pocHtmlPath, captureScreenshots, cancellationToken);
        }
        catch (Exception ex)
        {
            // Một lỗi hạ tầng browser (crash, timeout) không được làm hỏng lượt audit của agent.
            _logger.LogWarning(ex, "POC runtime check failed for {Path}.", pocHtmlPath);
            return PocRuntimeReport.Skipped($"lỗi khi chạy kiểm tra ({ex.Message}).");
        }
    }

    private static async Task<PocRuntimeReport> RunCheckAsync(IBrowser browser, string pocHtmlPath, bool captureScreenshots, CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        var screenshots = new List<PocScreenshot>();

        // Viewport desktop cố định để ảnh chụp phản ánh đúng bố cục enterprise dashboard mà spec yêu cầu.
        await using var context = await browser.NewContextAsync(captureScreenshots
            ? new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 } }
            : null);
        var page = await context.NewPageAsync();

        // Gom lỗi JS thật: pageerror (exception chưa bắt) + console.error KHÔNG PHẢI lỗi tải tài nguyên
        // (Bootstrap nạp từ CDN — môi trường offline sẽ fail các request đó nhưng không phải lỗi của POC).
        var errors = new List<string>();
        var currentScreen = "(tải trang)";
        page.PageError += (_, message) =>
        {
            lock (errors) errors.Add($"[{currentScreen}] Lỗi JS: {FirstLine(message)}");
        };
        page.Console += (_, msg) =>
        {
            if (msg.Type != "error")
                return;
            var text = msg.Text;
            if (text.Contains("Failed to load resource", StringComparison.OrdinalIgnoreCase)
                || text.Contains("net::ERR", StringComparison.OrdinalIgnoreCase))
                return;
            lock (errors) errors.Add($"[{currentScreen}] console.error: {FirstLine(text)}");
        };

        var url = new Uri(Path.GetFullPath(pocHtmlPath)).AbsoluteUri;
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = GotoTimeoutMs });
        await page.WaitForTimeoutAsync(300);
        cancellationToken.ThrowIfCancellationRequested();

        // Đi qua TỪNG màn hình bằng đúng cơ chế của shell (pocNavigate) — phát hiện màn hình click
        // không mở được và lỗi JS chỉ nổ khi màn hình đó hiện ra.
        var screens = await page.EvaluateAsync<string[]>(
            "() => Array.from(document.querySelectorAll('section.page-view')).map(s => (s.dataset.view || '').trim()).filter(Boolean)");

        foreach (var screen in screens.Take(MaxScreens))
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentScreen = screen;

            // Mở màn hình VÀ đo luôn "độ đầy" của nó trong một lần evaluate: text hiển thị + số phần tử
            // nội dung (control/bảng/card…) của section đang active. Đây là heuristic màn-hình-trống KHÔNG
            // cần vision agent — lớp lỗi mà scan chuỗi/self-test đều mù (màn mở được nhưng rỗng trơn).
            var probe = await page.EvaluateAsync<ScreenProbe?>(
                @"(label) => {
                    try {
                        if (typeof window.pocNavigate === 'function') window.pocNavigate(label);
                        const active = document.querySelector('section.page-view.active');
                        const opened = !!active && (active.dataset.view || '').trim().toLowerCase() === label.trim().toLowerCase();
                        const overflow = Math.max(0, document.documentElement.scrollWidth - window.innerWidth);
                        if (!active) return { opened: opened, textLen: 0, controls: 0, overflowX: overflow };
                        const text = (active.innerText || '').replace(/\s+/g, ' ').trim();
                        const controls = active.querySelectorAll('h1,h2,h3,h4,h5,p,input,select,textarea,button,table,canvas,svg,img,.card,li').length;
                        return { opened: opened, textLen: text.length, controls: controls, overflowX: overflow };
                    } catch { return { opened: false, textLen: 0, controls: 0, overflowX: 0 }; }
                }", screen);
            await page.WaitForTimeoutAsync(100);

            if (probe is not { Opened: true })
            {
                issues.Add($"Màn hình '{screen}' không mở được qua pocNavigate — click menu tương ứng sẽ không đổi nội dung (kiểm tra nhãn data-view khớp nhãn menu và script không ném lỗi khi render).");
                continue; // màn hình không mở được thì ảnh chụp vô nghĩa (vẫn ở màn cũ).
            }

            // Màn mở được nhưng KHÔNG có phần tử nội dung nào (không heading/đoạn văn/control/bảng/card) và
            // gần như không chữ = section rỗng trơn — báo issue để Developer dựng nội dung thật. Precision
            // cao (controls < 1): chỉ bắt màn thực sự trống, không đụng màn tối giản có tiêu đề.
            if (probe.TextLen < 15 && probe.Controls < 1)
                issues.Add($"Màn hình '{screen}' gần như TRỐNG (chỉ {probe.TextLen} ký tự hiển thị, không có phần tử nội dung nào) — dựng nội dung thật cho màn này theo AI Design Spec (bảng/thẻ/form + dữ liệu mẫu), đừng để một section rỗng.");

            // Tràn NGANG ở bề rộng desktop = layout không co giãn (bảng quá rộng không bọc table-responsive,
            // phần tử fixed-width tràn khung). Ngưỡng nới (>24px) để bỏ qua chênh lệch scrollbar lặt vặt.
            else if (probe.OverflowX > 24)
                issues.Add($"Màn hình '{screen}' bị TRÀN NGANG {probe.OverflowX}px ở bề rộng desktop (phải cuộn ngang) — bọc bảng rộng trong `table-responsive`, dùng lưới/flex co giãn thay cho phần tử cố định bề rộng, để layout không vỡ trên màn hẹp.");

            if (captureScreenshots && screenshots.Count < MaxScreens)
            {
                try
                {
                    var png = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });
                    screenshots.Add(new PocScreenshot(screen, png));
                }
                catch
                {
                    // Chụp lỗi một màn không được làm hỏng cả lượt check — bỏ qua ảnh màn đó.
                }
            }
        }

        currentScreen = "(pocSelfTest)";

        // window.pocSelfTest(): mỗi Business Rule một assertion do agent sinh — trả về mảng chuỗi đã
        // format sẵn để không phụ thuộc serializer object của Playwright.
        var selfTest = await page.EvaluateAsync<string[]?>(
            @"() => {
                if (typeof window.pocSelfTest !== 'function') return null;
                let result;
                try { result = window.pocSelfTest(); }
                catch (e) { return ['FAIL|pocSelfTest|ném lỗi khi chạy: ' + (e && e.message ? e.message : e)]; }
                if (!Array.isArray(result)) return ['FAIL|pocSelfTest|không trả về mảng [{rule, pass, detail}]'];
                return result.map(x => (x && x.pass ? 'PASS' : 'FAIL') + '|' + String((x && x.rule) || '?') + '|' + String((x && x.detail) || ''));
            }");

        var selfTestResults = new List<string>();
        if (selfTest != null)
        {
            foreach (var entry in selfTest)
            {
                var parts = entry.Split('|', 3);
                var pass = parts[0] == "PASS";
                var rule = parts.Length > 1 ? parts[1] : "?";
                var detail = parts.Length > 2 ? parts[2] : string.Empty;
                selfTestResults.Add($"{(pass ? "PASS" : "FAIL")} {rule}{(detail.Length > 0 ? $" — {detail}" : "")}");
                if (!pass)
                    issues.Add($"Self-test business rule FAIL: {rule}{(detail.Length > 0 ? $" — {detail}" : "")}. Sửa logic trong SetPocScript cho tới khi assertion này pass (đừng sửa assertion cho pass giả).");
            }
        }

        // window.pocWorkedExamples(): POC tự tính lại từng worked example rồi trả [{ref, computed}]. Đây là
        // NGUYÊN LIỆU cho oracle độc lập — WorkspaceTools đối chiếu `computed` với `Expected` của spec (kỳ
        // vọng do người dùng chốt). Chỉ thu THÔ ở đây (giá trị POC tính), so khớp để tầng có spec làm.
        currentScreen = "(pocWorkedExamples)";
        var workedRaw = await page.EvaluateAsync<string[]?>(
            @"() => {
                if (typeof window.pocWorkedExamples !== 'function') return null;
                let result;
                try { result = window.pocWorkedExamples(); }
                catch (e) { return ['__ERROR__|' + (e && e.message ? e.message : e)]; }
                if (!Array.isArray(result)) return ['__ERROR__|không trả về mảng [{ref, computed}]'];
                return result.map(x => String((x && x.ref) || '?') + '|' + String((x && (x.computed !== undefined ? x.computed : x.value)) ?? ''));
            }");

        var workedResults = new List<PocWorkedExampleResult>();
        if (workedRaw != null)
        {
            foreach (var entry in workedRaw)
            {
                var parts = entry.Split('|', 2);
                if (parts[0] == "__ERROR__")
                {
                    issues.Add($"window.pocWorkedExamples() ném lỗi/không hợp lệ: {(parts.Length > 1 ? parts[1] : "?")}. Định nghĩa hàm GLOBAL trả [{{ ref:'WE-n', computed:<giá trị POC tính> }}] cho từng worked example của spec.");
                    continue;
                }
                workedResults.Add(new PocWorkedExampleResult(parts[0], parts.Length > 1 ? parts[1] : string.Empty));
            }
        }

        lock (errors) issues.AddRange(errors);
        return new PocRuntimeReport(true, null, issues.Take(MaxIssues).ToList(), selfTestResults, screenshots, workedResults);
    }

    // Kết quả evaluate "mở màn hình + đo độ đầy" — Playwright deserialize JSON theo các key camelCase.
    private sealed class ScreenProbe
    {
        [System.Text.Json.Serialization.JsonPropertyName("opened")] public bool Opened { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("textLen")] public int TextLen { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("controls")] public int Controls { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("overflowX")] public int OverflowX { get; set; }
    }

    private async Task<IBrowser?> GetBrowserAsync()
    {
        if (_browser != null)
            return _browser;
        if (_launchFailure != null)
            return null; // đã thử và fail — đừng tốn thời gian launch lại ở mỗi vòng audit.

        await _lock.WaitAsync();
        try
        {
            if (_browser != null || _launchFailure != null)
                return _browser;

            _playwright ??= await Playwright.CreateAsync();

            var executablePath = _configuration["Poc:RuntimeCheck:BrowserPath"]
                ?? Environment.GetEnvironmentVariable("POC_BROWSER_PATH");

            var options = new BrowserTypeLaunchOptions { Headless = true };
            if (!string.IsNullOrWhiteSpace(executablePath))
                options.ExecutablePath = executablePath;

            _browser = await _playwright.Chromium.LaunchAsync(options);
            return _browser;
        }
        catch (Exception ex)
        {
            _launchFailure = FirstLine(ex.Message);
            _logger.LogWarning(ex, "Chromium headless is not available — POC runtime checks will be skipped.");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string FirstLine(string text)
    {
        var idx = text.IndexOf('\n');
        return (idx < 0 ? text : text[..idx]).Trim();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
        _lock.Dispose();
    }
}
