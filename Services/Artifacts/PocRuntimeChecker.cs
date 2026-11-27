using ICOGenerator.Contracts.Requirements;
using Microsoft.Playwright;

namespace ICOGenerator.Services.Artifacts;

/// <summary>Ảnh chụp một màn hình POC (PNG) để tầng Visual QA (vision model) chấm bố cục/dữ liệu mẫu.</summary>
public sealed record PocScreenshot(string Screen, byte[] Png);

/// <summary>
/// Kết quả LÁI THẬT một kịch bản nghiệm thu (UAT) trên POC: đi từng bước, tìm đúng nút/điều khiển được
/// nhắc tới rồi CLICK và xem màn hình có đổi gì không. Khác <c>window.pocScenarios()</c> (agent tự viết,
/// tự chấm bằng cách gọi hàm nội bộ), lớp này chạm vào đúng thứ người dùng sẽ chạm.
/// </summary>
public sealed record PocUatDriveResult(string Title, bool Pass, string Detail);

/// <summary>Một bảng dữ liệu đọc được từ màn hình POC — nguyên liệu cho <see cref="PocCrossScreenConsistency"/>.</summary>
public sealed record PocScreenTable(string Screen, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Kết quả một lần kiểm tra runtime POC. <see cref="Ran"/> = false nghĩa là bị bỏ qua (không có browser/tắt cấu hình) — audit vẫn tiếp tục với phần tĩnh.</summary>
public sealed record PocRuntimeReport(
    bool Ran,
    string? SkipReason,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> SelfTestResults,
    IReadOnlyList<PocScreenshot> Screenshots,
    IReadOnlyList<string> ScenarioResults,
    IReadOnlyList<PocUatDriveResult> UatDriveResults,
    IReadOnlyList<PocScreenTable> ScreenTables)
{
    public static PocRuntimeReport Skipped(string reason) =>
        new(false, reason, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<PocScreenshot>(),
            Array.Empty<string>(),
            Array.Empty<PocUatDriveResult>(), Array.Empty<PocScreenTable>());
}

/// <summary>
/// Kiểm tra RUNTIME của poc-demo.html — tầng mà <see cref="PocAudit"/> (scan chuỗi) không với tới:
/// một TypeError trong script nghiệp vụ làm chết toàn bộ tương tác nhưng audit tĩnh vẫn "OK".
/// Mở file trong Chromium headless, đi qua TỪNG màn hình bằng chính pocNavigate của shell, gom lỗi
/// JS (pageerror + console error), và chạy window.pocSelfTest() — bộ assertion mỗi Business Rule mà
/// prompt POC yêu cầu agent tự sinh — để rule fail thành ISSUE cụ thể thay vì lời tự khai.
/// <para>
/// <c>captureScreenshots</c> = true còn chụp ảnh TỪNG màn hình để tầng Visual QA (vision
/// model) chấm bố cục/dữ liệu mẫu — thứ mà cả scan chuỗi lẫn self-test đều "mù" (màn hình trống trơn,
/// layout vỡ vẫn pass). Chỉ bật khi có agent UI/UX vision để không chụp phí.
/// </para>
/// </summary>
public interface IPocRuntimeChecker
{
    /// <param name="uatScenarios">
    /// Bộ kịch bản nghiệm thu sinh TRƯỚC khi dựng POC (xem <see cref="Requirements.UatScenarioService"/>):
    /// runtime lái thật từng bước bằng click để bắt "nút chết"/"thiếu điều khiển" — lớp lỗi mà assertion
    /// do chính agent viết không bao giờ tự tố. Rỗng/null ⇒ bỏ qua lượt lái, hành vi như cũ.
    /// </param>
    Task<PocRuntimeReport> CheckAsync(
        string pocHtmlPath,
        bool captureScreenshots = false,
        IReadOnlyList<UatScenario>? uatScenarios = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Hiện thực bằng Microsoft.Playwright. FAIL-OPEN toàn phần: môi trường không có Chromium (hoặc tắt qua
/// <c>Poc:RuntimeCheck:Enabled</c>) thì trả Skipped kèm lý do — audit POC vẫn chạy phần tĩnh như cũ,
/// không bao giờ chặn pipeline vì thiếu browser. Đường tìm browser: cấu hình
/// <c>Poc:RuntimeCheck:BrowserPath</c> → biến môi trường <c>POC_BROWSER_PATH</c> → bộ browser Playwright
/// đã cài (PLAYWRIGHT_BROWSERS_PATH) → nếu chưa cài thì TỰ TẢI một lần (<c>Poc:RuntimeCheck:AutoInstall</c>,
/// mặc định bật) để máy mới chỉ cần clone source là chạy được. Browser được giữ lại dùng chung (singleton)
/// vì audit chạy tới 3 vòng mỗi POC — mỗi lần launch lại tốn ~nửa giây.
/// </summary>
public sealed class PlaywrightPocRuntimeChecker : IPocRuntimeChecker, IPocBrowserProbe, IAsyncDisposable
{
    private const int MaxScreens = 20;
    private const int MaxIssues = 20;
    // Ảnh trạng thái tương tác (mở modal) để Visual QA chấm — trần thấp vì chỉ cần vài mẫu để bắt lỗi
    // "modal mở ra bị vỡ layout" (lớp lỗi mà ảnh tĩnh mỗi màn không thấy). Xem R-POC2.
    private const int MaxModalShots = 3;
    private const float GotoTimeoutMs = 15000;
    // Lượt kiểm ở bề rộng ĐIỆN THOẠI (iPhone 12/13/14 logic width) — bề rộng mà người được gửi link demo
    // hay mở nhất, và là nơi layout cố định bề rộng lộ ra ngay.
    private const int MobileWidth = 390;
    private const int MobileOverflowThreshold = 32;
    private const int MaxMobileShots = 3;

    // Lái UAT: lái HẾT bộ kịch bản (trần của chính bộ đó) — cổng UAT chặn bàn giao theo từng kịch bản, nên
    // kịch bản không được lái là kịch bản cổng mù. Một kịch bản tốn một lần tải trang + vài trăm ms mỗi thao
    // tác (dừng sớm ngay khi trang phản ứng), tức vài giây cho cả bộ.
    private const int MaxUatScenarios = Requirements.UatScenarioService.MaxScenarios;
    private const int MaxUatSteps = 12;

    // Trích bảng cho phép đối chiếu chéo màn hình: đủ để bắt dữ liệu mẫu lệch nhau, không đủ để phình bộ nhớ.
    private const int MaxTablesPerScreen = 3;
    private const int MaxTablesTotal = 24;
    private const int MaxTableRows = 25;
    private const int MaxTableColumns = 8;
    private const int MaxTableCellChars = 60;

    private readonly IConfiguration _configuration;
    private readonly ILogger<PlaywrightPocRuntimeChecker> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Nhắc đúng lệnh cài trong log VÀ trong lý do skip hiện trên trang POC Review: người gặp dòng
    // "không khởi động được Chromium headless" biết ngay phải gõ gì, khỏi đi tra tài liệu.
    private const string InstallHint = "pwsh bin/Debug/net8.0/playwright.ps1 install chromium";

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private string? _launchFailure;
    private bool _installAttempted;

    public PlaywrightPocRuntimeChecker(IConfiguration configuration, ILogger<PlaywrightPocRuntimeChecker> logger)
    {
        _configuration = configuration;
        _logger = logger;
        // Phải chốt thư mục cache TRƯỚC khi Playwright khởi động/tự tải — xem PlaywrightBrowserCache.
        PlaywrightBrowserCache.Ensure(configuration);
    }

    public async Task<PocRuntimeReport> CheckAsync(
        string pocHtmlPath,
        bool captureScreenshots = false,
        IReadOnlyList<UatScenario>? uatScenarios = null,
        CancellationToken cancellationToken = default)
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
            return await RunCheckAsync(browser, pocHtmlPath, captureScreenshots, uatScenarios ?? Array.Empty<UatScenario>(), cancellationToken);
        }
        catch (Exception ex)
        {
            // Một lỗi hạ tầng browser (crash, timeout) không được làm hỏng lượt audit của agent.
            _logger.LogWarning(ex, "POC runtime check failed for {Path}.", pocHtmlPath);
            return PocRuntimeReport.Skipped($"lỗi khi chạy kiểm tra ({ex.Message}).");
        }
    }

    public async Task<PocBrowserProbeResult> ProbeAsync()
    {
        var executablePath = ConfiguredExecutablePath();
        var browser = await GetBrowserAsync();
        return browser is not null
            ? new PocBrowserProbeResult(true, browser.Version, executablePath, null)
            : new PocBrowserProbeResult(false, null, executablePath, _launchFailure);
    }

    private string? ConfiguredExecutablePath()
    {
        var path = _configuration["Poc:RuntimeCheck:BrowserPath"] ?? Environment.GetEnvironmentVariable("POC_BROWSER_PATH");
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static async Task<PocRuntimeReport> RunCheckAsync(
        IBrowser browser, string pocHtmlPath, bool captureScreenshots,
        IReadOnlyList<UatScenario> uatScenarios, CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        var screenshots = new List<PocScreenshot>();
        var screenTables = new List<PocScreenTable>();
        var modalShots = 0;

        // Viewport desktop cố định để ảnh chụp phản ánh đúng bố cục enterprise dashboard mà spec yêu cầu.
        await using var context = await browser.NewContextAsync(captureScreenshots
            ? new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 } }
            : null);
        // Bắt VỊ TRÍ lỗi JS (dòng/cột trong tài liệu) — PageError của Playwright chỉ có thông điệp, mà lỗi cú
        // pháp thì không có cả stack. Chạy trước mọi script của trang, ở mọi lần tải lại. Xem PocScriptErrorLocator.
        await context.AddInitScriptAsync(ErrorCaptureScript);
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
        var errorLocations = await LocateErrorsAsync(page, pocHtmlPath);

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

            // Đọc các bảng dữ liệu của màn này để tầng PocCrossScreenConsistency đối chiếu CHÉO giữa các
            // màn hình: cùng một bản ghi hiện ở hai nơi với con số/trạng thái khác nhau là lỗi người xem
            // demo bắt ngay, mà mọi cổng hiện có đều mù (mỗi màn xét riêng thì màn nào cũng "hợp lệ").
            await CollectScreenTablesAsync(page, screen, screenTables);

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

                // R-POC2: ảnh TRẠNG THÁI TƯƠNG TÁC — mở một modal của màn này rồi chụp, để Visual QA bắt lỗi
                // "hộp thoại mở ra bị vỡ/trống" mà ảnh tĩnh không thấy. Best-effort: Bootstrap nạp từ CDN nên
                // môi trường offline sẽ không mở được modal (không chụp) — không sao. Luôn đóng lại (Escape)
                // để không che các màn chụp sau.
                if (modalShots < MaxModalShots && screenshots.Count < MaxScreens)
                    modalShots += await TryCaptureModalAsync(page, screen, screenshots);
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

        // window.pocScenarios(): mỗi KỊCH BẢN nghiệp vụ END-TO-END (nhiều bước, đi qua nhiều màn hình/vai
        // trò) một phần tử {id/title, pass, detail}. Lớp lỗi mà pocSelfTest (rule cô lập) không thấy: luồng
        // GÃY GIỮA các màn hình — bấm "Duyệt" ở màn A nhưng trạng thái/danh sách ở màn B không đổi. Hàm do
        // agent lái chính POC qua từng bước rồi assert trạng thái cuối; dòng FAIL thành issue ở PocUatCoverage.
        currentScreen = "(pocScenarios)";
        var scenarioRaw = await page.EvaluateAsync<string[]?>(
            @"() => {
                if (typeof window.pocScenarios !== 'function') return null;
                let result;
                try { result = window.pocScenarios(); }
                catch (e) { return ['FAIL|pocScenarios|ném lỗi khi chạy: ' + (e && e.message ? e.message : e)]; }
                if (!Array.isArray(result)) return ['FAIL|pocScenarios|không trả về mảng [{id/title, pass, detail}]'];
                return result.map(x => (x && x.pass ? 'PASS' : 'FAIL') + '|' + String((x && (x.title || x.id)) || '?') + '|' + String((x && x.detail) || ''));
            }");

        var scenarioResults = new List<string>();
        if (scenarioRaw != null)
        {
            foreach (var entry in scenarioRaw)
            {
                var parts = entry.Split('|', 3);
                var pass = parts[0] == "PASS";
                var name = parts.Length > 1 ? parts[1] : "?";
                var detail = parts.Length > 2 ? parts[2] : string.Empty;
                // Chỉ ghi SỰ KIỆN, không tự sinh issue: PocUatCoverage gộp dòng FAIL này với kết quả bấm thử
                // của cùng kịch bản UAT thành MỘT issue (một kịch bản gãy không được tính hai-ba lần).
                scenarioResults.Add($"{(pass ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? $" — {detail}" : "")}");
            }
        }

        // LÁI THẬT bộ kịch bản nghiệm thu (UAT): mỗi kịch bản mở lại POC từ đầu (trạng thái sạch), đi từng
        // bước, tìm đúng nút/điều khiển mà bước đó nhắc tới rồi CLICK. Đây là tầng duy nhất kiểm POC bằng
        // thao tác của NGƯỜI DÙNG thay vì bằng assertion do chính agent viết — nên nó bắt được đúng lớp lỗi
        // hay làm hỏng buổi demo: nhãn trong kịch bản không tồn tại trên màn hình, và nút bấm không làm gì cả.
        var uatResults = await DriveUatScenariosAsync(page, url, uatScenarios, errors, label => currentScreen = label, cancellationToken);

        // LƯỢT CLICK MENU: bấm THẬT vào từng mục menu đang hiển thị và xem <main> có đổi đúng màn không.
        // Vòng đi màn hình ở trên gọi window.pocNavigate() bằng JS nên nó MÙ với lớp lỗi "click menu chết":
        // menu bị script nghiệp vụ dựng lại (mô phỏng vai) làm mất handler, hoặc handler riêng của script gọi
        // pocNavigate ngay trong lúc xử lý click của chính mục đó. Chạy sau lượt lái UAT để bắt được cả menu
        // SAU khi đăng nhập/đổi vai, chứ không chỉ menu lúc mới tải.
        currentScreen = "(click menu)";
        await CheckNavClickRoutingAsync(page, issues, cancellationToken);

        // LƯỢT ĐỔI VAI: bấm THẬT từng vai ở khối VIEW AS (cuối sidebar) và xem vai đó có gì để xem không.
        // Trước đây vai được mô phỏng bằng một màn đăng nhập giả do script tự dựng, nên mọi cổng ở trên
        // chỉ nhìn thấy đúng cái form đó: menu rỗng, một vai không mở được màn nào, hay bấm vai mà không
        // đổi gì đều lọt. Nay vai là cơ chế của shell nên kiểm được bằng đúng thao tác người xem demo làm.
        currentScreen = "(đổi vai)";
        await CheckRoleSwitchingAsync(page, issues, cancellationToken);

        // LƯỢT ĐIỆN THOẠI: mở lại POC ở bề rộng 390px và đi qua từng màn hình. Trước đây mọi thứ chỉ được
        // kiểm ở 1440px nên lớp lỗi "vỡ trên màn hẹp" KHÔNG cổng nào bắt được — kể cả Visual QA, vì nó chỉ
        // nhìn ảnh desktop (dù prompt của nó có mục "phải cuộn ngang"). Đây là bề rộng mà người duyệt hay
        // mở demo nhất khi được gửi link.
        await RunMobilePassAsync(browser, url, screens, issues, screenshots, captureScreenshots, cancellationToken);

        // Distinct: lượt lái UAT tải lại trang nhiều lần, nên một lỗi JS lúc tải trang sẽ được ghi lại ở
        // mỗi lượt — người sửa chỉ cần thấy nó MỘT lần.
        lock (errors) issues.AddRange(errors.Distinct(StringComparer.Ordinal));
        // Đứng ĐẦU danh sách: một lỗi cú pháp làm chết cả script, mọi issue khác chỉ là hệ quả của nó.
        issues.InsertRange(0, errorLocations);
        return new PocRuntimeReport(
            true, null, issues.Take(MaxIssues).ToList(), selfTestResults, screenshots, scenarioResults,
            uatResults, screenTables);
    }

    // Bấm thật từng mục menu lá đang hiển thị (bỏ tiêu đề nhóm và mục mở popup) rồi so màn hình đang active
    // với nhãn mục đó. Chỉ xét mục CÓ section tương ứng: mục không có section là lỗi wiring, đã có cổng khác
    // báo, xét ở đây chỉ tạo báo động trùng. Best-effort: evaluate hỏng ⇒ bỏ qua, không chặn audit.
    private static async Task CheckNavClickRoutingAsync(IPage page, List<string> issues, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] dead;
        try
        {
            dead = await page.EvaluateAsync<string[]>(
                """
                async () => {
                  const norm = s => (s || '').toLowerCase().replace(/\s+/g, ' ').trim();
                  const labelOf = n => { const el = n.querySelector('.nav-label'); return el ? el.textContent.trim() : ''; };
                  const sections = Array.from(document.querySelectorAll('section.page-view'));
                  const views = sections.map(v => norm(v.dataset.view));
                  if (!sections.length) return [];
                  const items = Array.from(document.querySelectorAll('.sidebar-nav .nav-item')).filter(n =>
                    !(n.parentElement && n.parentElement.classList.contains('nav-group')) && !n.hasAttribute('data-bs-toggle'));
                  const bad = [];
                  for (const item of items) {
                    const label = labelOf(item);
                    if (!label || views.indexOf(norm(label)) < 0) continue;
                    // Đỗ sẵn ở MỘT màn KHÁC trước khi bấm (đổi class trực tiếp, không nhờ code của trang):
                    // nếu màn đích đang mở sẵn thì mục menu chết vẫn "trông như" chạy đúng.
                    const other = sections.find(v => norm(v.dataset.view) !== norm(label));
                    if (other) sections.forEach(v => v.classList.toggle('active', v === other));
                    try { item.click(); } catch { }
                    await new Promise(r => setTimeout(r, 60));
                    const active = document.querySelector('section.page-view.active');
                    const shown = active ? norm(active.dataset.view) : '';
                    if (shown !== norm(label)) bad.push(label + '|' + (shown || '(không màn nào hiện)'));
                  }
                  return bad;
                }
                """);
        }
        catch
        {
            return;
        }

        foreach (var entry in dead.Take(MaxIssues))
        {
            var parts = entry.Split('|', 2);
            issues.Add(
                $"CLICK mục menu '{parts[0]}' KHÔNG đổi nội dung — vẫn đang hiện màn '{(parts.Length > 1 ? parts[1] : "?")}'. "
                + "Đây đúng là thứ người xem demo bấm đầu tiên. Để shell tự lo điều hướng: ĐỪNG gắn handler click "
                + "riêng cho mục menu và đừng gọi pocNavigate() từ trong handler click của chính mục đó; "
                + "dựng lại menu (mô phỏng vai) thì chỉ thay nội dung <nav class=\"sidebar-nav\">, shell đã bắt click bằng delegation.");
        }
    }

    // Đi qua từng vai của khối VIEW AS: bấm nút vai → vai hiện tại phải đổi thật, sidebar phải còn ít
    // nhất một mục để bấm, và màn hình đang mở phải là màn vai đó được phép thấy. Ít hơn hai vai (POC một
    // actor) ⇒ không có gì để kiểm. Best-effort như các lượt khác: evaluate hỏng thì bỏ qua.
    private static async Task CheckRoleSwitchingAsync(IPage page, List<string> issues, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] bad;
        try
        {
            bad = await page.EvaluateAsync<string[]>(
                """
                async () => {
                  const norm = s => (s || '').toLowerCase().replace(/\s+/g, ' ').trim();
                  const items = Array.from(document.querySelectorAll('#viewAsList .view-as-item'));
                  if (items.length < 2) return [];
                  const rolesOf = el => (el && el.getAttribute ? (el.getAttribute('data-roles') || '') : '')
                    .split(',').map(norm).filter(Boolean);
                  // "Đang hiện" theo nghĩa CỦA VAI: mục bị vai ẩn (display none) hoặc nằm trong nhóm bị ẩn.
                  // Không dùng kích thước hiển thị — nhóm accordion đang đóng không phải là "vai không thấy".
                  const shownForRole = n => n.style.display !== 'none'
                    && !(n.closest('.nav-group') && n.closest('.nav-group').style.display === 'none');
                  const out = [];
                  for (const item of items) {
                    const label = ((item.getAttribute('data-role') || item.textContent) || '').trim();
                    try { item.click(); } catch { }
                    await new Promise(r => setTimeout(r, 80));
                    const now = (typeof window.pocRole === 'function') ? window.pocRole() : '';
                    if (norm(now) !== norm(label)) { out.push(label + '|switch|' + (now || '(không đổi)')); continue; }
                    const leaves = Array.from(document.querySelectorAll('.sidebar-nav .nav-item')).filter(n =>
                      !(n.parentElement && n.parentElement.classList.contains('nav-group')) && shownForRole(n));
                    if (!leaves.length) { out.push(label + '|empty|'); continue; }
                    const view = document.querySelector('section.page-view.active');
                    const allowed = view ? rolesOf(view) : [];
                    if (view && allowed.length && allowed.indexOf(norm(label)) < 0)
                      out.push(label + '|screen|' + (view.dataset.view || ''));
                  }
                  if (items.length) { try { items[0].click(); } catch { } }
                  return out;
                }
                """);
        }
        catch
        {
            return;
        }

        foreach (var entry in bad.Take(MaxIssues))
        {
            var parts = entry.Split('|', 3);
            var role = parts[0];
            var detail = parts.Length > 2 ? parts[2] : string.Empty;
            issues.Add(parts.Length > 1 && parts[1] == "empty"
                ? $"Chọn vai '{role}' ở VIEW AS xong thì SIDEBAR TRỐNG — vai này không mở được màn nào. Gắn vai vào data-roles của ít nhất một mục menu (mục không khai báo data-roles thì mọi vai đều thấy), hoặc bỏ vai này khỏi danh sách roles của SetPocContent."
                : parts.Length > 1 && parts[1] == "screen"
                    ? $"Đổi sang vai '{role}' nhưng màn hình đang mở vẫn là '{detail}' — màn này khai báo data-roles KHÔNG có vai đó, tức người xem demo đang nhìn màn hình của vai khác. Để shell tự mở màn đầu tiên của vai: đừng tự điều hướng trong window.pocOnRoleChange."
                    : $"Bấm vai '{role}' ở VIEW AS KHÔNG đổi được vai (window.pocRole() trả '{detail}'). Danh sách vai do SetPocContent('roles') dựng — đừng tự dựng lại khối VIEW AS hay tự gắn handler click cho .view-as-item.");
        }
    }

    // Đọc bảng dữ liệu của màn hình đang mở (tối đa MaxTablesPerScreen bảng): tiêu đề cột + các dòng, cắt
    // ngắn từng ô. Best-effort: màn không có bảng / evaluate lỗi ⇒ không thu gì, tầng đối chiếu tự bỏ qua.
    private static async Task CollectScreenTablesAsync(IPage page, string screen, List<PocScreenTable> sink)
    {
        if (sink.Count >= MaxTablesTotal)
            return;

        try
        {
            var json = await page.EvaluateAsync<string>(
                """
                (limits) => {
                  const active = document.querySelector('section.page-view.active');
                  if (!active) return '[]';
                  const clean = t => (t || '').replace(/\s+/g, ' ').trim().slice(0, limits.cell);
                  const out = [];
                  const tables = Array.from(active.querySelectorAll('table')).slice(0, limits.tables);
                  for (const table of tables) {
                    const headers = Array.from(table.querySelectorAll('thead th')).map(th => clean(th.innerText)).slice(0, limits.cols);
                    if (headers.length === 0) continue;
                    const rows = [];
                    for (const tr of Array.from(table.querySelectorAll('tbody tr')).slice(0, limits.rows)) {
                      const cells = Array.from(tr.querySelectorAll('td,th')).map(td => clean(td.innerText)).slice(0, limits.cols);
                      if (cells.some(c => c.length > 0)) rows.push(cells);
                    }
                    if (rows.length > 0) out.push({ headers: headers, rows: rows });
                  }
                  return JSON.stringify(out);
                }
                """,
                new { tables = MaxTablesPerScreen, rows = MaxTableRows, cols = MaxTableColumns, cell = MaxTableCellChars });

            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<ScreenTableDto>>(json ?? "[]");
            if (parsed == null)
                return;

            foreach (var table in parsed)
            {
                if (sink.Count >= MaxTablesTotal)
                    return;
                sink.Add(new PocScreenTable(
                    screen,
                    table.Headers ?? new List<string>(),
                    (table.Rows ?? new List<List<string>>()).Select(r => (IReadOnlyList<string>)r).ToList()));
            }
        }
        catch
        {
            // Đọc bảng là lớp bổ sung — hỏng thì bỏ qua màn này, phần còn lại của runtime check vẫn nguyên.
        }
    }

    // Lái từng kịch bản nghiệm thu bằng THAO TÁC THẬT. Mỗi kịch bản chạy trên một lần tải trang mới để
    // trạng thái của kịch bản trước không "rớt" sang kịch bản sau. Bộ lái (PocUatDriver.js) chỉ báo SỰ KIỆN;
    // UatScenarioDrive chấm, và chỉ kết luận hai khuyết tật KHÔNG THỂ CHỐI CÃI khi trang còn ở đúng trạng thái
    // kịch bản giả định (mọi thao tác trước đều làm được):
    //   • thứ cần thao tác (vai, màn, nút, dòng) không có ở đâu trên POC;
    //   • bấm đúng điều khiển đó mà màn hình KHÔNG đổi gì (nút chết) — toast của shell được loại khỏi phép
    //     so sánh vì shell tự toast cho mọi .btn, kể cả nút chưa nối logic.
    // Bộ kịch bản có kế hoạch thao tác (UatScenario.StepActions) thì máy làm ĐÚNG các thao tác đó — mở màn,
    // bấm, GÕ giá trị vào ô theo nhãn, chọn, nhìn. Bộ cũ chỉ có câu chữ thì lùi về đoán một điều khiển mỗi bước.
    private static async Task<List<PocUatDriveResult>> DriveUatScenariosAsync(
        IPage page, string url, IReadOnlyList<UatScenario> scenarios,
        List<string> errors, Action<string> setCurrentScreen, CancellationToken cancellationToken)
    {
        var results = new List<PocUatDriveResult>();
        if (scenarios.Count == 0)
            return results;

        // Mức trang (không phải context): chỉ các lần tải trang của lượt lái này cần bộ lái.
        await page.AddInitScriptAsync(PocUatDriver.Script);

        for (var index = 0; index < scenarios.Count && index < MaxUatScenarios; index++)
        {
            var scenario = scenarios[index];
            cancellationToken.ThrowIfCancellationRequested();
            setCurrentScreen($"UAT: {scenario.Title}");

            int errorsBefore;
            lock (errors) errorsBefore = errors.Count;
            var drive = new UatScenarioDrive(scenario.Title, () => { lock (errors) return errors.Count; });
            string? crash = null;

            try
            {
                // Trạng thái sạch cho mỗi kịch bản: một kịch bản "duyệt đơn" chạy sau kịch bản "gửi đơn"
                // mà không reset thì đang kiểm trên dữ liệu đã bị kịch bản trước làm biến dạng.
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = GotoTimeoutMs });
                await page.WaitForTimeoutAsync(250);

                // Bộ cũ: mở sẵn màn chính để lượt đoán theo câu có chỗ bám. Bộ có kế hoạch thì KHÔNG — kế hoạch
                // tự chọn vai và mở màn từ trang người dùng thấy đầu tiên, đúng đường họ sẽ đi.
                if (!scenario.HasActionPlan && !string.IsNullOrWhiteSpace(scenario.Screen))
                {
                    await page.EvaluateAsync(
                        "(label) => { try { if (typeof window.pocNavigate === 'function') window.pocNavigate(label); } catch {} }",
                        scenario.Screen);
                    await page.WaitForTimeoutAsync(120);
                }

                for (var i = 0; i < scenario.Steps.Count && i < MaxUatSteps && drive.Failure == null; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var step = scenario.Steps[i];
                    // Neo do agent khai báo (data-uat="{kịch bản}.{bước}") là đích CHÍNH XÁC của bước này.
                    var anchor = UatAnchor.Token(index, i);
                    var actions = scenario.ActionsOf(i);

                    if (actions.Count == 0)
                    {
                        drive.RecordLegacyStep(i, step, await RunLegacyStepAsync(page, step, anchor));
                        continue;
                    }

                    // Neo của bước chỉ gắn được cho MỘT phần tử: bước có đúng một thao tác bấm thì neo là của nó;
                    // bước bấm nhiều thứ thì đi theo nhãn từng thao tác.
                    var clicks = actions.Count(a => a.Kind == UatActionKind.Click);
                    foreach (var action in actions)
                    {
                        var actionAnchor = action.Kind == UatActionKind.Click && clicks == 1 ? anchor : null;
                        drive.RecordAction(i, step, action, await RunUatActionAsync(page, action, actionAnchor));
                        if (drive.Failure != null)
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                crash = $"không lái được kịch bản ({FirstLine(ex.Message)})";
            }

            // Lỗi JS phát sinh TRONG lúc lái (khi trạng thái còn tin được) là bằng chứng mạnh nhất: người dùng
            // bấm đúng kịch bản thì trang vỡ. Không tự sinh issue — xem PocUatCoverage.Check: kết quả này được
            // gộp với mục pocScenarios() của cùng kịch bản thành MỘT issue.
            string[] snapshot;
            lock (errors) snapshot = errors.ToArray();
            results.Add(drive.Finish(snapshot, errorsBefore, crash));
        }

        return results;
    }

    private static async Task<UatDriveOutcome> RunUatActionAsync(IPage page, UatAction action, string? anchor)
    {
        try
        {
            var json = await page.EvaluateAsync<string?>(
                "([a, anchor]) => window.__pocUat ? window.__pocUat.act(a, anchor) : null",
                new object?[] { new { kind = action.Kind, target = action.Target, value = action.Value }, anchor });
            return Parse(json) ?? UatDriveOutcome.Unknown("bộ lái không nạp được vào trang");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Trang đang điều hướng giữa chừng, evaluate hỏng… — không làm được, KHÔNG phải lỗi POC.
            return UatDriveOutcome.Unknown($"không lái được ({FirstLine(ex.Message)})");
        }
    }

    private static async Task<UatDriveOutcome> RunLegacyStepAsync(IPage page, string step, string anchor)
    {
        try
        {
            var json = await page.EvaluateAsync<string?>(
                "([step, anchor]) => window.__pocUat ? window.__pocUat.legacy(step, anchor) : null",
                new object?[] { step, anchor });
            return Parse(json) ?? UatDriveOutcome.Unknown("bộ lái không nạp được vào trang");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UatDriveOutcome.Unknown($"không lái được ({FirstLine(ex.Message)})");
        }
    }

    private static UatDriveOutcome? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<UatDriveOutcome>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // Lượt kiểm ở bề rộng điện thoại: chỉ đo TRÀN NGANG (lỗi khách quan, không phải sở thích thẩm mỹ) và
    // chụp vài ảnh cho Visual QA. Best-effort toàn phần: lượt này lỗi thì lượt desktop ở trên vẫn nguyên
    // giá trị, nên mọi ngoại lệ đều bị nuốt.
    private static async Task RunMobilePassAsync(
        IBrowser browser, string url, string[] screens, List<string> issues,
        List<PocScreenshot> screenshots, bool captureScreenshots, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = MobileWidth, Height = 844 }
            });
            var page = await context.NewPageAsync();
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = GotoTimeoutMs });
            await page.WaitForTimeoutAsync(300);

            var shots = 0;
            foreach (var screen in screens.Take(MaxScreens))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var overflow = await page.EvaluateAsync<int>(
                    @"(label) => {
                        try {
                            if (typeof window.pocNavigate === 'function') window.pocNavigate(label);
                            return Math.max(0, document.documentElement.scrollWidth - window.innerWidth);
                        } catch { return 0; }
                    }", screen);
                await page.WaitForTimeoutAsync(80);

                // Ngưỡng rộng tay hơn desktop (>32px): ở 390px một chút lệch do scrollbar/viền là bình
                // thường; chỉ báo khi thật sự phải kéo ngang mới đọc hết nội dung.
                if (overflow > MobileOverflowThreshold)
                    issues.Add($"Màn hình '{screen}' bị TRÀN NGANG {overflow}px ở bề rộng điện thoại ({MobileWidth}px) — người xem demo trên điện thoại phải kéo ngang mới đọc hết. Bọc bảng rộng trong `table-responsive`, dùng `row`/`col-*` để các cột xuống dòng trên màn hẹp, bỏ bề rộng cố định.");

                if (captureScreenshots && shots < MaxMobileShots && screenshots.Count < MaxScreens + MaxMobileShots)
                {
                    try
                    {
                        var png = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });
                        screenshots.Add(new PocScreenshot($"{screen} (điện thoại {MobileWidth}px)", png));
                        shots++;
                    }
                    catch
                    {
                        // Ảnh một màn không chụp được thì bỏ qua màn đó.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Lượt điện thoại là lớp kiểm BỔ SUNG — hỏng thì im lặng bỏ qua, không đụng kết quả desktop.
        }
    }

    // Mở modal đầu tiên của màn đang active, chụp ảnh trạng thái mở (cho Visual QA), rồi đóng lại. Trả về
    // 1 nếu đã chụp được một ảnh modal, 0 nếu không có modal / không mở được (offline / lỗi). Best-effort:
    // mọi lỗi được nuốt — một ảnh tương tác không chụp được không được làm hỏng lượt runtime check.
    private static async Task<int> TryCaptureModalAsync(IPage page, string screen, List<PocScreenshot> screenshots)
    {
        try
        {
            var opened = await page.EvaluateAsync<bool>(
                @"() => {
                    const active = document.querySelector('section.page-view.active');
                    if (!active) return false;
                    const trigger = active.querySelector('[data-bs-toggle=""modal""]');
                    if (!trigger) return false;
                    trigger.click();
                    return true;
                }");
            if (!opened)
                return 0;

            await page.WaitForTimeoutAsync(350);
            var shown = await page.EvaluateAsync<bool>("() => !!document.querySelector('.modal.show')");
            var captured = 0;
            if (shown)
            {
                var png = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });
                screenshots.Add(new PocScreenshot($"{screen} (hộp thoại)", png));
                captured = 1;
            }

            // Đóng modal: Escape (Bootstrap lắng nghe) rồi chờ hiệu ứng để màn chụp sau không bị che.
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForTimeoutAsync(250);
            return captured;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Một bảng đọc từ màn hình POC (JSON của evaluate) trước khi gắn tên màn hình.</summary>
    private sealed class ScreenTableDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("headers")] public List<string>? Headers { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("rows")] public List<List<string>>? Rows { get; set; }
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

            var executablePath = ConfiguredExecutablePath();

            var options = new BrowserTypeLaunchOptions { Headless = true };
            if (!string.IsNullOrWhiteSpace(executablePath))
                options.ExecutablePath = executablePath;

            try
            {
                _browser = await _playwright.Chromium.LaunchAsync(options);
            }
            catch (Exception ex) when (ShouldAttemptInstall(executablePath, ex.Message, AutoInstallEnabled))
            {
                // Máy mới clone source về: package NuGet có sẵn nhưng binary Chromium thì chưa (nằm ở
                // cache dùng chung của máy, không nằm trong repo — xem README §3.6). Tải một lần rồi thử
                // lại thay vì bắt người dùng đọc lý do skip trên trang review mới biết mình phải cài gì.
                if (!await TryInstallChromiumAsync())
                    throw;
                _browser = await _playwright.Chromium.LaunchAsync(options);
            }

            return _browser;
        }
        catch (Exception ex)
        {
            _launchFailure = FirstLine(ex.Message);
            if (IsMissingBrowserError(ex.Message))
                _launchFailure += $" — cài bằng: {InstallHint}";
            _logger.LogWarning(ex, "Chromium headless is not available — POC runtime checks will be skipped.");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool AutoInstallEnabled => _configuration.GetValue("Poc:RuntimeCheck:AutoInstall", true);

    /// <summary>
    /// Có nên tự tải bộ browser rồi thử lại không. Chỉ đúng khi CẢ BA điều kiện cùng đúng: bật cấu hình,
    /// KHÔNG có đường dẫn browser chỉ định sẵn (đã chỉ đường mà sai thì tải về cũng không dùng tới), và
    /// lỗi launch đúng dạng "chưa có binary". Lỗi khác — thiếu thư viện hệ điều hành, sandbox chặn — tải
    /// 150MB về cũng không chữa được, nên fail-open ngay như cũ.
    /// </summary>
    internal static bool ShouldAttemptInstall(string? explicitExecutablePath, string launchError, bool autoInstallEnabled)
        => autoInstallEnabled
           && string.IsNullOrWhiteSpace(explicitExecutablePath)
           && IsMissingBrowserError(launchError);

    private static bool IsMissingBrowserError(string message)
        => message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
           || message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tải bộ Chromium của Playwright (tương đương <c>playwright.ps1 install chromium</c>) vào cache dùng
    /// chung của máy. Chỉ thử MỘT lần mỗi process: hết hạn chờ hoặc lỗi mạng thì bỏ qua, audit chạy tiếp
    /// phần tĩnh — không bao giờ chặn pipeline vì tải browser hỏng.
    /// </summary>
    private async Task<bool> TryInstallChromiumAsync()
    {
        if (_installAttempted)
            return false;
        _installAttempted = true;

        var timeout = TimeSpan.FromSeconds(Math.Max(30, _configuration.GetValue("Poc:RuntimeCheck:AutoInstallTimeoutSeconds", 300)));
        _logger.LogInformation(
            "Headless Chromium not found — downloading the Playwright browser bundle (once per machine, ~150MB, up to {Timeout}).",
            timeout);

        try
        {
            // Program.Main là API cài đặt chính thức của Microsoft.Playwright; nó chạy đồng bộ nên đẩy
            // sang thread pool để không chặn caller. Quá hạn thì bỏ mặc lượt tải chạy nốt dưới nền — lần
            // khởi động app sau sẽ thấy binary đã có sẵn.
            var install = Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
            if (await Task.WhenAny(install, Task.Delay(timeout)) != install)
            {
                _logger.LogWarning("Chromium download exceeded {Timeout} — skipping this round, the POC is only checked statically.", timeout);
                return false;
            }

            var exitCode = await install;
            if (exitCode != 0)
            {
                _logger.LogWarning("Chromium download command returned exit code {ExitCode}. Install manually with: {Hint}", exitCode, InstallHint);
                return false;
            }

            _logger.LogInformation("Headless Chromium downloaded — re-running the POC runtime checks.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not download headless Chromium. Install manually with: {Hint}", InstallHint);
            return false;
        }
    }

    private const string ErrorCaptureScript =
        "window.__pocErrs=[];window.addEventListener('error',function(e){try{if(e&&e.message)"
        + "window.__pocErrs.push({message:String(e.message),line:e.lineno|0,column:e.colno|0});}catch(_){}},true);";

    // Lỗi lúc tải trang (lỗi cú pháp nằm ở đây) kèm dòng code của vùng POC_SCRIPT. Fail-open: đọc không
    // được thì chỉ mất phần vị trí, dòng "Lỗi JS" chung vẫn còn.
    private static async Task<IReadOnlyList<string>> LocateErrorsAsync(IPage page, string pocHtmlPath)
    {
        try
        {
            var found = await page.EvaluateAsync<JsErrorPosition[]?>("() => window.__pocErrs || []");
            if (found is not { Length: > 0 })
                return [];

            var html = await File.ReadAllTextAsync(pocHtmlPath);
            return found
                .Select(e => PocScriptErrorLocator.Describe(html, e.Line, e.Column, e.Message ?? string.Empty))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Take(5)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    // Tên property TRÙNG tên khoá JS (không phân biệt hoa thường): bộ đọc kết quả của Playwright .NET bỏ qua
    // JsonPropertyName, đặt tên khác là mọi trường về 0.
    private sealed class JsErrorPosition
    {
        public string? Message { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
    }

    internal static string FirstLine(string text)
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
