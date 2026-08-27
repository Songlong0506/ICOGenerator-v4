using ICOGenerator.Contracts.Requirements;
using Microsoft.Playwright;

namespace ICOGenerator.Services.Artifacts;

/// <summary>Ảnh chụp một màn hình POC (PNG) để tầng Visual QA (vision model) chấm bố cục/dữ liệu mẫu.</summary>
public sealed record PocScreenshot(string Screen, byte[] Png);

/// <summary>Kết quả POC tự tính một worked example (window.pocWorkedExamples): ref + giá trị POC tính ra.</summary>
public sealed record PocWorkedExampleResult(string Ref, string Computed);

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
    IReadOnlyList<PocWorkedExampleResult> WorkedExampleResults,
    IReadOnlyList<string> ScenarioResults,
    IReadOnlyList<PocUatDriveResult> UatDriveResults,
    IReadOnlyList<PocScreenTable> ScreenTables)
{
    public static PocRuntimeReport Skipped(string reason) =>
        new(false, reason, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<PocScreenshot>(),
            Array.Empty<PocWorkedExampleResult>(), Array.Empty<string>(),
            Array.Empty<PocUatDriveResult>(), Array.Empty<PocScreenTable>());
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
public sealed class PlaywrightPocRuntimeChecker : IPocRuntimeChecker, IAsyncDisposable
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

    // Lái UAT: mỗi kịch bản tốn một lần tải trang + ~0,4s mỗi bước, và audit chạy tới 3 vòng mỗi POC —
    // trần giữ tổng chi phí ở mức vài giây thay vì làm bước POC dài ra thấy rõ.
    private const int MaxUatScenarios = 5;
    private const int MaxUatSteps = 6;

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
        // agent lái chính POC qua từng bước rồi assert trạng thái cuối; fail thành ISSUE như self-test.
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
                scenarioResults.Add($"{(pass ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? $" — {detail}" : "")}");
                if (!pass)
                    issues.Add($"Kịch bản nghiệp vụ (end-to-end) FAIL: {name}{(detail.Length > 0 ? $" — {detail}" : "")}. Sửa logic để luồng chạy ĐÚNG qua các màn hình (trạng thái sau mỗi bước phản ánh nhất quán trên mọi màn liên quan) — đừng sửa assertion cho pass giả.");
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

        // LÁI THẬT bộ kịch bản nghiệm thu (UAT): mỗi kịch bản mở lại POC từ đầu (trạng thái sạch), đi từng
        // bước, tìm đúng nút/điều khiển mà bước đó nhắc tới rồi CLICK. Đây là tầng duy nhất kiểm POC bằng
        // thao tác của NGƯỜI DÙNG thay vì bằng assertion do chính agent viết — nên nó bắt được đúng lớp lỗi
        // hay làm hỏng buổi demo: nhãn trong kịch bản không tồn tại trên màn hình, và nút bấm không làm gì cả.
        var uatResults = await DriveUatScenariosAsync(page, url, uatScenarios, issues, errors, label => currentScreen = label, cancellationToken);

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
        return new PocRuntimeReport(
            true, null, issues.Take(MaxIssues).ToList(), selfTestResults, screenshots, workedResults, scenarioResults,
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
    // trạng thái của kịch bản trước không "rớt" sang kịch bản sau. Kết luận chỉ nêu hai loại khuyết tật
    // KHÔNG THỂ CHỐI CÃI, để cổng này không biến thành máy sinh báo động giả:
    //   • nhãn được nhắc trong ngoặc kép của bước không tồn tại ở đâu trên POC (thiếu điều khiển);
    //   • bấm đúng điều khiển đó mà màn hình KHÔNG đổi gì (nút chết) — toast của shell được loại khỏi phép
    //     so sánh vì shell tự toast cho mọi .btn, kể cả nút chưa nối logic.
    // Bước mang tính QUAN SÁT ("kiểm tra đơn ở trạng thái Chờ duyệt") không tìm thấy điều khiển thì bỏ qua,
    // không tính là lỗi. Cùng lý do, bấm lại điều khiển ĐANG active (vai đang chọn, mục menu đang mở)
    // cũng là bỏ qua chứ không phải "nút chết": no-op ở đó là hành vi đúng.
    private static async Task<List<PocUatDriveResult>> DriveUatScenariosAsync(
        IPage page, string url, IReadOnlyList<UatScenario> scenarios,
        List<string> issues, List<string> errors, Action<string> setCurrentScreen, CancellationToken cancellationToken)
    {
        var results = new List<PocUatDriveResult>();
        if (scenarios.Count == 0)
            return results;

        for (var index = 0; index < scenarios.Count && index < MaxUatScenarios; index++)
        {
            var scenario = scenarios[index];
            cancellationToken.ThrowIfCancellationRequested();
            setCurrentScreen($"UAT: {scenario.Title}");

            int errorsBefore;
            lock (errors) errorsBefore = errors.Count;

            string? failure = null;
            var actionsDone = 0;

            try
            {
                // Trạng thái sạch cho mỗi kịch bản: một kịch bản "duyệt đơn" chạy sau kịch bản "gửi đơn"
                // mà không reset thì đang kiểm trên dữ liệu đã bị kịch bản trước làm biến dạng.
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = GotoTimeoutMs });
                await page.WaitForTimeoutAsync(250);

                if (!string.IsNullOrWhiteSpace(scenario.Screen))
                {
                    await page.EvaluateAsync(
                        "(label) => { try { if (typeof window.pocNavigate === 'function') window.pocNavigate(label); } catch {} }",
                        scenario.Screen);
                    await page.WaitForTimeoutAsync(120);
                }

                for (var i = 0; i < scenario.Steps.Count && i < MaxUatSteps && failure == null; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var step = scenario.Steps[i];
                    // Neo do agent khai báo (data-uat="{kịch bản}.{bước}") là đích CHÍNH XÁC của bước này;
                    // lượt khớp nhãn bên dưới chỉ còn là đường lùi cho POC chưa gắn neo.
                    var outcome = await RunUatStepAsync(page, step, UatAnchor.Token(index, i));

                    switch (outcome.Status)
                    {
                        case "missing":
                            failure = $"bước {i + 1} (\"{Shorten(step)}\"): không tìm thấy điều khiển nào mang nhãn '{outcome.Label}' trên POC";
                            break;
                        case "dead":
                            failure = $"bước {i + 1} (\"{Shorten(step)}\"): bấm '{outcome.Label}' nhưng màn hình không thay đổi gì (nút chưa nối logic)";
                            break;
                        case "clicked":
                            actionsDone++;
                            break;
                        // "skip" (bước quan sát, không có điều khiển tương ứng) và "nobootstrap" (modal
                        // không mở được vì Bootstrap nạp từ CDN mà môi trường offline) — không kết luận.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure ??= $"không lái được kịch bản ({FirstLine(ex.Message)})";
            }

            // Lỗi JS phát sinh TRONG lúc lái là bằng chứng mạnh nhất: người dùng bấm đúng kịch bản thì trang vỡ.
            if (failure == null)
            {
                string[] newErrors;
                lock (errors) newErrors = errors.Skip(errorsBefore).ToArray();
                if (newErrors.Length > 0)
                    failure = $"lỗi JS khi thao tác: {FirstLine(newErrors[0])}";
            }

            var pass = failure == null;
            var detail = pass
                ? $"{actionsDone} thao tác chạy được, không lỗi"
                : failure!;
            results.Add(new PocUatDriveResult(scenario.Title, pass, detail));

            if (!pass)
                issues.Add(
                    $"Kịch bản nghiệm thu (UAT) '{scenario.Title}' KHÔNG thao tác được trên POC — {detail}. "
                    + "Đây là kịch bản người dùng sẽ bấm thử khi nghiệm thu: sửa cho đúng nhãn/điều khiển và nối logic thật cho nút đó.");
        }

        return results;
    }

    // Một bước kịch bản: tìm điều khiển ứng với bước → chụp trạng thái → click → chờ → chụp lại → so.
    // Toàn bộ chạy trong MỘT evaluate (async) để không phải đi lại nhiều vòng giữa C# và trang.
    private static async Task<UatStepOutcome> RunUatStepAsync(IPage page, string step, string anchor)
    {
        try
        {
            var json = await page.EvaluateAsync<string>(
                """
                async (arg) => {
                  const step = arg.step;
                  const anchor = arg.anchor;
                  const norm = s => (s || '').toLowerCase().replace(/\s+/g, ' ').trim();
                  const visible = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
                  // Nhãn của một phần tử ĐANG BỊ ẩn (mục menu trong nhóm chưa xổ) có innerText rỗng, nên
                  // phải lùi về textContent — nếu không thì đúng các màn hình vừa được gom nhóm lại biến
                  // mất khỏi tầm nhìn của lượt lái.
                  const labelOf = el => norm(el.innerText || el.value || el.textContent || '');
                  const snapshot = () => {
                    const active = document.querySelector('section.page-view.active');
                    const modals = document.querySelectorAll('.modal.show');
                    // Chỉ lấy text của màn đang mở + modal đang mở: toast của shell nằm ngoài (#toastHost)
                    // nên KHÔNG lọt vào chữ ký — shell tự toast cho mọi .btn, tính vào thì nút chết cũng "có phản ứng".
                    let text = active ? (active.innerText || '') : '';
                    modals.forEach(m => { text += '|' + (m.innerText || ''); });
                    let h = 0;
                    for (let i = 0; i < text.length; i++) { h = ((h * 31) + text.charCodeAt(i)) | 0; }
                    return { view: active ? (active.dataset.view || '') : '', hash: h, modals: modals.length };
                  };

                  const stepText = norm(step);
                  const quoted = [];
                  const re = /["'“”‘’]([^"'“”‘’]{2,60})["'“”‘’]/g;
                  let m;
                  while ((m = re.exec(step)) !== null) quoted.push(m[1].trim());

                  const clickable = Array.from(document.querySelectorAll(
                    'button, a, [role=button], input[type=submit], input[type=button], .btn, [data-crud-add]'))
                    .filter(el => visible(el) && labelOf(el).length >= 2);

                  // MỤC MENU là <div class="nav-item"> (PocTemplate.RenderNav) nên không lọt vào tập
                  // trên: thiếu nó thì bước "Mở màn hình X" — bước mở đầu của gần như mọi kịch bản
                  // nghiệm thu — không bao giờ khớp được điều khiển nào. Tiêu đề NHÓM bị loại vì nó chỉ
                  // xổ/thu, không mở màn nào (đúng luật isNavGroupHead của shell), và mục con của nhóm
                  // chưa xổ vẫn tính là bấm được: shell bắt click bằng delegation rồi tự mở nhóm.
                  // Chỉ vai không được xem mới thật sự nằm ngoài tầm với — đó là lúc applyRoleToNav
                  // đặt display:none thẳng lên mục hoặc lên cả nhóm.
                  const navLeaves = Array.from(document.querySelectorAll('.sidebar-nav .nav-item'))
                    .filter(el => !(el.parentElement && el.parentElement.classList.contains('nav-group')))
                    .filter(el => el.style.display !== 'none'
                              && !(el.closest('.nav-group') && el.closest('.nav-group').style.display === 'none'))
                    .filter(el => labelOf(el).length >= 2);

                  const controls = clickable.concat(navLeaves);

                  // Nhãn trùng TÊN MỘT MÀN HÌNH (data-view) thì điều khiển đúng là mục menu, không phải
                  // một nút cùng tên trong nội dung: bước "Mở màn hình 'Duyệt đơn'" ở một POC có sẵn nút
                  // "Duyệt đơn" trên bảng phải mở màn, chứ không phải duyệt đại một bản ghi.
                  const screenNames = Array.from(document.querySelectorAll('section.page-view'))
                    .map(s => norm(s.dataset.view || ''));
                  const findControl = nq => {
                    const pool = screenNames.indexOf(nq) >= 0 ? navLeaves.concat(clickable) : controls;
                    return pool.find(el => labelOf(el) === nq) || pool.find(el => labelOf(el).includes(nq));
                  };

                  // Chữ để phán "nhãn này có tồn tại trên trang không": phần nhìn thấy được, CỘNG nhãn
                  // trong sidebar (mục của nhóm chưa xổ không nằm trong innerText). Cố ý KHÔNG dùng
                  // body.textContent — nó nuốt luôn mã JS của shell, và khi đó không nhãn nào còn bị coi
                  // là thiếu nữa.
                  const nav = document.querySelector('.sidebar-nav');
                  const pageText = norm((document.body.innerText || '') + ' ' + (nav ? nav.textContent || '' : ''));

                  let target = null;
                  let label = '';
                  let quotedTried = 0;

                  // NEO CHỈ CHỖ trước, đoán nhãn sau. Agent dựng POC đã khai báo phần tử của TỪNG bước
                  // (data-uat="{kịch bản}.{bước}", xem UatAnchor/PocUatAnchors), nên ở đây không cần suy
                  // ra điều khiển từ câu tiếng Việt nữa — suy ra là nguồn của cả lớp "bấm trúng nút khác
                  // vì nhãn nó là khúc con của câu bước".
                  //
                  // Chỉ nhận neo trỏ vào thứ BẤM ĐƯỢC: bước kiểm tra neo vào ô trạng thái/dòng bảng (đúng
                  // theo quy ước), click nó không chứng minh gì mà lại bị chấm "nút chết". Neo không bấm
                  // được ⇒ rơi xuống lượt khớp nhãn y như trước, nên đường neo chỉ thêm độ chính xác chứ
                  // không cắt mất độ phủ của cổng này.
                  if (anchor) {
                    const anchored = document.querySelector('[data-uat~="' + anchor + '"]');
                    const clickableSel = 'button, a, [role=button], input[type=submit], input[type=button], .btn,'
                      + ' [data-crud-add], .sidebar-nav .nav-item, .view-as-item';
                    if (anchored && visible(anchored)) {
                      const hit = anchored.matches(clickableSel) ? anchored : anchored.querySelector(clickableSel);
                      if (hit && visible(hit)) {
                        target = hit;
                        label = labelOf(hit) || anchor;
                      }
                    }
                  }

                  if (!target) for (const q of quoted) {
                    const nq = norm(q);
                    if (nq.length < 2) continue;
                    quotedTried++;
                    target = findControl(nq);
                    label = q;
                    if (target) break;
                    // Nhãn có tồn tại ở đâu đó trên trang (tiêu đề cột, chữ trong bảng) thì chưa kết luận
                    // thiếu — bước có thể đang nói về dữ liệu chứ không phải nút bấm.
                    if (!pageText.includes(nq)) return JSON.stringify({ status: 'missing', label: q });
                  }

                  if (!target && quotedTried > 0) {
                    // Bước ĐÃ nêu nhãn trong ngoặc, nhãn đó có trên trang nhưng không phải điều khiển ⇒
                    // bước đang nói về DỮ LIỆU. Rơi xuống khớp ngược ở đây là cách cổng này từng bấm bừa
                    // một nút khác chỉ vì nhãn nó là khúc con của câu bước (nút vai "HRBP" trong bước
                    // "Mở màn hình 'HRBP Approval'") rồi chấm cú no-op đó là nút chết.
                    return JSON.stringify({ status: 'skip', label: '' });
                  }

                  if (!target) {
                    // Không có nhãn trong ngoặc: thử khớp NGƯỢC — nhãn nút nào xuất hiện nguyên văn trong câu bước.
                    let best = null, bestLen = 0;
                    for (const el of controls) {
                      const t = labelOf(el);
                      if (t.length >= 3 && t.length > bestLen && stepText.includes(t)) { best = el; bestLen = t.length; }
                    }
                    if (!best) return JSON.stringify({ status: 'skip', label: '' });
                    target = best;
                    label = labelOf(target);
                  }

                  // Điều khiển ĐIỀU HƯỚNG đang sẵn ở trạng thái bước yêu cầu (vai đang chọn, mục menu
                  // đang mở) thì không có gì để chứng minh: bấm lại là no-op ĐÚNG. Chấm nó "nút chết" sẽ
                  // đánh trượt mọi kịch bản mở đầu bằng "chọn vai <vai đầu tiên>" — mà vai đầu tiên
                  // chính là vai demo mở lên.
                  const stateful = target.closest ? target.closest('.view-as-item, .sidebar-nav .nav-item') : null;
                  if (stateful && stateful.classList.contains('active'))
                    return JSON.stringify({ status: 'skip', label: label });

                  const before = snapshot();
                  const roleBefore = (typeof window.pocRole === 'function') ? window.pocRole() : null;
                  try { target.click(); } catch (e) { return JSON.stringify({ status: 'skip', label: label }); }
                  await new Promise(r => setTimeout(r, 350));
                  const after = snapshot();
                  let changed = before.view !== after.view || before.hash !== after.hash || before.modals !== after.modals;
                  // Bước "chọn vai X" bấm vào khối VIEW AS: bằng chứng nó có tác dụng là VAI đã đổi, không
                  // phải chữ trên màn đổi — hai vai hoàn toàn có thể cùng mở một màn hình đầu tiên, và
                  // chấm theo chữ sẽ báo oan "nút chết" cho đúng cái nút thay thế màn đăng nhập.
                  if (!changed && roleBefore !== null && target.closest && target.closest('.view-as-item'))
                    changed = norm(window.pocRole()) !== norm(roleBefore);
                  if (!changed && target.getAttribute && target.getAttribute('data-bs-toggle') === 'modal' && !window.bootstrap)
                    return JSON.stringify({ status: 'nobootstrap', label: label });
                  return JSON.stringify({ status: changed ? 'clicked' : 'dead', label: label });
                }
                """, new { step, anchor });

            return System.Text.Json.JsonSerializer.Deserialize<UatStepOutcome>(json ?? "{}") ?? new UatStepOutcome();
        }
        catch
        {
            // Bước không lái được (selector lạ, trang đang điều hướng) thì bỏ qua bước đó — không kết luận lỗi.
            return new UatStepOutcome();
        }
    }

    private static string Shorten(string text)
    {
        var clean = (text ?? string.Empty).Replace('\n', ' ').Trim();
        return clean.Length <= 80 ? clean : clean[..77] + "…";
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

    /// <summary>Kết quả một bước lái UAT: status ∈ clicked|dead|missing|skip|nobootstrap + nhãn điều khiển liên quan.</summary>
    private sealed class UatStepOutcome
    {
        [System.Text.Json.Serialization.JsonPropertyName("status")] public string Status { get; set; } = "skip";
        [System.Text.Json.Serialization.JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
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

            var executablePath = _configuration["Poc:RuntimeCheck:BrowserPath"]
                ?? Environment.GetEnvironmentVariable("POC_BROWSER_PATH");

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
            "Chưa có Chromium headless — đang tải bộ browser Playwright (một lần cho mỗi máy, ~150MB, tối đa {Timeout}).",
            timeout);

        try
        {
            // Program.Main là API cài đặt chính thức của Microsoft.Playwright; nó chạy đồng bộ nên đẩy
            // sang thread pool để không chặn caller. Quá hạn thì bỏ mặc lượt tải chạy nốt dưới nền — lần
            // khởi động app sau sẽ thấy binary đã có sẵn.
            var install = Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
            if (await Task.WhenAny(install, Task.Delay(timeout)) != install)
            {
                _logger.LogWarning("Tải Chromium quá {Timeout} — bỏ qua vòng này, POC chỉ được kiểm tĩnh.", timeout);
                return false;
            }

            var exitCode = await install;
            if (exitCode != 0)
            {
                _logger.LogWarning("Lệnh tải Chromium trả về mã lỗi {ExitCode}. Cài thủ công bằng: {Hint}", exitCode, InstallHint);
                return false;
            }

            _logger.LogInformation("Đã tải xong Chromium headless — chạy lại kiểm tra runtime POC.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không tải được Chromium headless. Cài thủ công bằng: {Hint}", InstallHint);
            return false;
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
