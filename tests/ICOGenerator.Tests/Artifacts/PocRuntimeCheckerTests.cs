using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Artifacts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

/// <summary>
/// Test chạy Chromium THẬT (headless) khi máy có browser — đường tìm: env POC_BROWSER_PATH hoặc
/// /opt/pw-browsers/chromium (môi trường CI/Claude web). Không có browser thì các test này chỉ xác
/// nhận hành vi fail-open (SKIPPED kèm lý do) — đúng hành vi production trên máy không cài Chromium.
/// </summary>
public class PocRuntimeCheckerTests : IAsyncLifetime
{
    private static readonly string? BrowserPath = FindBrowser();

    private readonly PlaywrightPocRuntimeChecker _checker;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "poc-runtime-tests", Guid.NewGuid().ToString("N"));

    public PocRuntimeCheckerTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Poc:RuntimeCheck:BrowserPath"] = BrowserPath
            })
            .Build();
        _checker = new PlaywrightPocRuntimeChecker(config, NullLogger<PlaywrightPocRuntimeChecker>.Instance);
        Directory.CreateDirectory(_dir);
    }

    private static string? FindBrowser()
    {
        var env = Environment.GetEnvironmentVariable("POC_BROWSER_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;
        const string claudeWebChromium = "/opt/pw-browsers/chromium";
        return File.Exists(claudeWebChromium) ? claudeWebChromium : null;
    }

    private async Task<PocRuntimeReport> CheckHtmlAsync(string html)
    {
        var path = Path.Combine(_dir, "poc-demo.html");
        await File.WriteAllTextAsync(path, html);
        return await _checker.CheckAsync(path);
    }

    private async Task<PocRuntimeReport> DriveAsync(string html, params UatScenario[] scenarios)
    {
        var path = Path.Combine(_dir, "poc-demo.html");
        await File.WriteAllTextAsync(path, html);
        return await _checker.CheckAsync(path, captureScreenshots: false, uatScenarios: scenarios);
    }

    private static UatScenario Scenario(string title, params string[] steps) =>
        new() { Title = title, Steps = steps.ToList() };

    private const string Shell = """
        <!doctype html><html><head><meta charset="utf-8"></head><body>
        <section class="page-view active" data-view="Trang chủ"><h1>Home</h1></section>
        <section class="page-view" data-view="Danh sách"><h1>List</h1></section>
        <script>
        window.pocNavigate = function (label) {
            document.querySelectorAll('section.page-view').forEach(function (s) {
                s.classList.toggle('active', (s.dataset.view || '').toLowerCase() === label.toLowerCase());
            });
        };
        </script>
        {SCRIPT}
        </body></html>
        """;

    [Fact]
    public async Task MissingFile_IsSkipped()
    {
        var report = await _checker.CheckAsync(Path.Combine(_dir, "khong-ton-tai.html"));

        Assert.False(report.Ran);
        Assert.NotNull(report.SkipReason);
    }

    [Fact]
    public async Task CleanPoc_PassingSelfTest_ReportsOk()
    {
        var report = await CheckHtmlAsync(Shell.Replace("{SCRIPT}", """
            <script>
            function pocSelfTest() {
                return [{ rule: 'BR-1', pass: 1 + 1 === 2, detail: 'cộng đúng' }];
            }
            </script>
            """));

        if (!report.Ran)
            return; // môi trường không có Chromium: fail-open là hành vi đúng, không còn gì để assert.

        Assert.Empty(report.Issues);
        Assert.Single(report.SelfTestResults);
        Assert.StartsWith("PASS", report.SelfTestResults[0]);
    }

    // Menu "chết": pocNavigate chạy tốt (lượt đi màn hình bằng JS pass sạch) nhưng CLICK vào mục menu
    // thì <main> không đổi — đúng lỗi của POC quản lý JD: script nghiệp vụ dựng lại sidebar sau khi đăng
    // nhập nên mục menu mất handler của shell, breadcrumb đổi mà nội dung vẫn nằm ở màn Đăng nhập.
    private const string SidebarShell = """
        <!doctype html><html><head><meta charset="utf-8"></head><body>
        <aside class="sidebar"><nav class="sidebar-nav">
          <div class="nav-item active"><span class="nav-label">Trang chủ</span></div>
          <div class="nav-item"><span class="nav-label">Danh sách</span></div>
        </nav></aside>
        <main class="page">
          <section class="page-view active" data-view="Trang chủ"><h1>Home</h1></section>
          <section class="page-view" data-view="Danh sách"><h1>List</h1></section>
        </main>
        <script>
        function showView(label) {
            document.querySelectorAll('section.page-view').forEach(function (s) {
                s.classList.toggle('active', (s.dataset.view || '').toLowerCase() === label.toLowerCase());
            });
        }
        window.pocNavigate = showView;
        {NAV_WIRING}
        </script>
        </body></html>
        """;

    [Fact]
    public async Task SidebarItem_ThatDoesNotSwitchView_BecomesIssue()
    {
        var report = await CheckHtmlAsync(SidebarShell.Replace("{NAV_WIRING}", ""));

        if (!report.Ran)
            return;

        Assert.Contains(report.Issues, i => i.Contains("CLICK mục menu") && i.Contains("Danh sách"));
    }

    [Fact]
    public async Task SidebarItem_WiredByDelegation_HasNoIssue()
    {
        var report = await CheckHtmlAsync(SidebarShell.Replace("{NAV_WIRING}", """
            document.addEventListener('click', function (e) {
                var item = e.target.closest('.sidebar-nav .nav-item');
                if (item) showView(item.querySelector('.nav-label').textContent.trim());
            });
            """));

        if (!report.Ran)
            return;

        Assert.DoesNotContain(report.Issues, i => i.Contains("CLICK mục menu"));
    }

    [Fact]
    public async Task FailingSelfTest_And_JsError_BecomeIssues()
    {
        var report = await CheckHtmlAsync(Shell.Replace("{SCRIPT}", """
            <script>
            undefinedFunctionCall();
            </script>
            <script>
            function pocSelfTest() {
                return [{ rule: 'BR-2', pass: false, detail: 'kỳ vọng 100, thực tế 90' }];
            }
            </script>
            """));

        if (!report.Ran)
            return;

        Assert.Contains(report.Issues, i => i.Contains("BR-2"));
        Assert.Contains(report.Issues, i => i.Contains("Lỗi JS"));
    }

    [Fact]
    public async Task PassingScenario_IsReportedAndNoIssue()
    {
        // window.pocScenarios(): kịch bản end-to-end (R-POC1) — pass thì vào ScenarioResults, không tạo issue.
        var report = await CheckHtmlAsync(Shell.Replace("{SCRIPT}", """
            <script>
            function pocScenarios() {
                return [{ title: 'Gửi rồi duyệt đơn', pass: true, detail: 'trạng thái cuối Đã duyệt' }];
            }
            </script>
            """));

        if (!report.Ran)
            return;

        Assert.Single(report.ScenarioResults);
        Assert.StartsWith("PASS", report.ScenarioResults[0]);
        Assert.DoesNotContain(report.Issues, i => i.Contains("Kịch bản nghiệp vụ"));
    }

    [Fact]
    public async Task FailingScenario_BecomesIssue()
    {
        var report = await CheckHtmlAsync(Shell.Replace("{SCRIPT}", """
            <script>
            function pocScenarios() {
                return [{ title: 'Duyệt đơn xuyên màn', pass: false, detail: 'màn nhân viên vẫn hiện Chờ duyệt' }];
            }
            </script>
            """));

        if (!report.Ran)
            return;

        Assert.Contains(report.Issues, i => i.Contains("Kịch bản nghiệp vụ") && i.Contains("Duyệt đơn xuyên màn"));
    }

    [Fact]
    public async Task CaptureScreenshots_ReturnsDesktopAndMobileShotsPerOpenedScreen()
    {
        var path = Path.Combine(_dir, "poc-demo.html");
        await File.WriteAllTextAsync(path, Shell.Replace("{SCRIPT}", ""));

        var report = await _checker.CheckAsync(path, captureScreenshots: true);
        if (!report.Ran)
            return; // không có Chromium: fail-open, không có gì để assert.

        // Hai màn hình mở được (Trang chủ + Danh sách) ⇒ hai ảnh desktop…
        var desktop = report.Screenshots.Where(s => !s.Screen.Contains("điện thoại")).ToList();
        Assert.Equal(2, desktop.Count);
        Assert.Contains(desktop, s => s.Screen == "Trang chủ");

        // …cộng ảnh ở bề rộng ĐIỆN THOẠI cho Visual QA: lớp lỗi "vỡ trên màn hẹp" trước đây không cổng
        // nào thấy vì mọi thứ chỉ được kiểm ở 1440px.
        Assert.Contains(report.Screenshots, s => s.Screen.Contains("điện thoại"));

        Assert.All(report.Screenshots, s => Assert.True(s.Png.Length > 0));
    }

    [Fact]
    public async Task WithoutCaptureFlag_NoScreenshots()
    {
        var report = await CheckHtmlAsync(Shell.Replace("{SCRIPT}", ""));
        if (!report.Ran)
            return;

        Assert.Empty(report.Screenshots);
    }

    [Fact]
    public async Task BlankSection_IsReported()
    {
        // Section mở được nhưng RỖNG TRƠN (không heading/nội dung) — heuristic màn-hình-trống (H).
        var html = """
            <!doctype html><html><head><meta charset="utf-8"></head><body>
            <section class="page-view active" data-view="Trống"></section>
            <script>
            window.pocNavigate = function (label) {
                document.querySelectorAll('section.page-view').forEach(function (s) {
                    s.classList.toggle('active', (s.dataset.view || '').toLowerCase() === label.toLowerCase());
                });
            };
            </script>
            </body></html>
            """;

        var report = await CheckHtmlAsync(html);
        if (!report.Ran)
            return;

        Assert.Contains(report.Issues, i => i.Contains("Trống") && i.Contains("TRỐNG"));
    }

    [Fact]
    public async Task BrokenNavigation_IsReported()
    {
        // pocNavigate ném lỗi → không màn hình nào mở được ngoài màn active sẵn.
        var html = """
            <!doctype html><html><head><meta charset="utf-8"></head><body>
            <section class="page-view" data-view="Báo cáo"></section>
            <script>window.pocNavigate = function () { throw new Error('hỏng'); };</script>
            </body></html>
            """;

        var report = await CheckHtmlAsync(html);
        if (!report.Ran)
            return;

        Assert.Contains(report.Issues, i => i.Contains("Báo cáo"));
    }

    // ---- Tự tải Chromium khi máy chưa có (ShouldAttemptInstall) ----
    // Quyết định "có tải không" được tách ra thuần tuý để test được mà không phải tải thật 150MB.

    private const string MissingBinaryError =
        "Executable doesn't exist at C:\\Users\\x\\AppData\\Local\\ms-playwright\\chromium_headless_shell-1228\\chrome-headless-shell.exe";

    [Fact]
    public void ShouldAttemptInstall_WhenBinaryMissing_AndNoExplicitPath()
    {
        Assert.True(PlaywrightPocRuntimeChecker.ShouldAttemptInstall(null, MissingBinaryError, autoInstallEnabled: true));
    }

    [Fact]
    public void ShouldAttemptInstall_IsFalse_WhenDisabledByConfig()
    {
        // Máy offline / CI có browser riêng: tắt cấu hình là không được tự ý tải gì về.
        Assert.False(PlaywrightPocRuntimeChecker.ShouldAttemptInstall(null, MissingBinaryError, autoInstallEnabled: false));
    }

    [Fact]
    public void ShouldAttemptInstall_IsFalse_WhenBrowserPathWasGiven()
    {
        // Đã chỉ đường dẫn browser mà vẫn fail ⇒ tải bộ Playwright về cũng không được dùng tới.
        Assert.False(PlaywrightPocRuntimeChecker.ShouldAttemptInstall(
            @"C:\Program Files\Edge\msedge.exe", MissingBinaryError, autoInstallEnabled: true));
    }

    [Fact]
    public void ShouldAttemptInstall_IsFalse_ForOtherLaunchFailures()
    {
        // Thiếu thư viện hệ điều hành: tải browser không chữa được, fail-open ngay thay vì tốn 150MB.
        Assert.False(PlaywrightPocRuntimeChecker.ShouldAttemptInstall(
            null, "Host system is missing dependencies to run browsers: libnss3.so", autoInstallEnabled: true));
    }

    [Fact]
    public async Task LaunchFailure_WithAutoInstallOff_StaysFailOpen()
    {
        // Đường dẫn browser trỏ vào file không tồn tại + tắt auto-install ⇒ SKIPPED kèm lý do, audit
        // tĩnh vẫn chạy. Đây là hành vi cũ và nó không được đổi.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Poc:RuntimeCheck:BrowserPath"] = Path.Combine(_dir, "khong-co-browser.exe"),
                ["Poc:RuntimeCheck:AutoInstall"] = "false"
            })
            .Build();
        await using var checker = new PlaywrightPocRuntimeChecker(config, NullLogger<PlaywrightPocRuntimeChecker>.Instance);

        var path = Path.Combine(_dir, "poc-demo.html");
        await File.WriteAllTextAsync(path, Shell.Replace("{SCRIPT}", ""));
        var report = await checker.CheckAsync(path);

        Assert.False(report.Ran);
        Assert.NotNull(report.SkipReason);
        Assert.Contains("Chromium", report.SkipReason);
    }

    // ===== VIEW AS trên SHELL THẬT =====
    // Các test dưới đây dựng POC từ chính Prompts/Design/poc-template.html rồi mở bằng Chromium: đây là
    // tầng duy nhất chứng minh cơ chế đổi vai của shell CHẠY THẬT (lọc menu, mở màn của vai, không ném
    // lỗi JS). Trước đây vai do script của agent tự dựng sau một màn đăng nhập giả nên không cổng nào
    // kiểm được nó.
    private static string PromptsRoot()
    {
        var fromBin = Path.Combine(AppContext.BaseDirectory, "Prompts");
        if (Directory.Exists(Path.Combine(fromBin, "Design")))
            return fromBin;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Prompts");
            if (Directory.Exists(Path.Combine(candidate, "Design")))
                return candidate;
        }

        throw new DirectoryNotFoundException("Không tìm thấy thư mục Prompts từ " + AppContext.BaseDirectory);
    }

    private static string RealShellPoc(string[] roles, List<PocNavItem> nav, string content)
    {
        var template = File.ReadAllText(Path.Combine(PromptsRoot(), "Design", "poc-template.html"));
        var html = PocTemplate.SeedFromTemplate(template)!;
        html = PocTemplate.ReplaceNav(html, nav);
        html = PocTemplate.ReplaceRoles(html, roles);
        return PocTemplate.ReplaceContent(html, content)!;
    }

    private static string Screen(string view, string? roles = null) =>
        $"<section class=\"page-view{(view == "Đơn của tôi" ? " active" : "")}\" data-view=\"{view}\""
        + (roles == null ? "" : $" data-roles=\"{roles}\"") + ">"
        + $"<h2 class=\"h4\">{view}</h2><p>Nội dung demo của màn {view} với đủ chữ để không bị coi là màn trống.</p>"
        + "<button class=\"btn btn-primary\">Gửi</button></section>";

    [Fact]
    public async Task RealShell_RoleSwitcher_FiltersMenuAndKeepsEveryScreenReachable()
    {
        var html = RealShellPoc(
            ["Nhân viên", "Quản lý"],
            [
                new PocNavItem { Label = "Đơn của tôi" },
                new PocNavItem { Label = "Duyệt đơn", Roles = ["Quản lý"] }
            ],
            Screen("Đơn của tôi") + Screen("Duyệt đơn", "Quản lý"));

        var report = await CheckHtmlAsync(html);
        if (!report.Ran)
            return;

        Assert.Empty(report.Issues);
    }

    [Fact]
    public async Task RealShell_RoleThatOpensNothing_BecomesIssue()
    {
        // Mọi mục menu đều thuộc vai "Nhân viên" ⇒ chọn "Quản lý" là sidebar trống trơn: lỗi người xem
        // demo gặp ngay ở cú bấm đầu tiên, và chỉ lượt đổi vai này thấy được.
        var html = RealShellPoc(
            ["Nhân viên", "Quản lý"],
            [
                new PocNavItem { Label = "Đơn của tôi", Roles = ["Nhân viên"] },
                new PocNavItem { Label = "Duyệt đơn", Roles = ["Nhân viên"] }
            ],
            Screen("Đơn của tôi") + Screen("Duyệt đơn"));

        var report = await CheckHtmlAsync(html);
        if (!report.Ran)
            return;

        Assert.Contains(report.Issues, i => i.Contains("SIDEBAR TRỐNG") && i.Contains("Quản lý"));
    }

    // MENU GOM NHÓM trên shell thật: các màn danh mục nằm trong một mục xổ xuống (nhóm KHÔNG mở sẵn,
    // vì nhóm đầu tiên mới nhận class "open") vẫn phải bấm được và mở đúng màn của nó. Đây là tầng duy
    // nhất chứng minh việc gom nhóm không làm chết chính lượt CLICK MENU mà nó vừa thu gọn.
    [Fact]
    public async Task RealShell_GroupedCatalogMenu_EveryChildStillOpensItsScreen()
    {
        var html = RealShellPoc(
            ["Nhân viên", "Quản lý"],
            [
                new PocNavItem { Label = "Đơn của tôi" },
                new PocNavItem
                {
                    Label = "Danh mục",
                    Children =
                    [
                        new PocNavItem { Label = "Skill Catalog" },
                        new PocNavItem { Label = "Degree Catalog" },
                        new PocNavItem { Label = "JobTitle Catalog" }
                    ]
                }
            ],
            Screen("Đơn của tôi") + Screen("Skill Catalog") + Screen("Degree Catalog") + Screen("JobTitle Catalog"));

        var report = await CheckHtmlAsync(html);
        if (!report.Ran)
            return;

        Assert.Empty(report.Issues);
    }

    // ===== LƯỢT BẤM THỬ THEO KỊCH BẢN NGHIỆM THU trên SHELL THẬT =====
    // Cả bốn test dưới đây chạy trên chính poc-template.html: đây là tầng duy nhất chứng minh lượt lái
    // nhìn thấy đúng những điều khiển mà người nghiệm thu sẽ bấm. Ba test đầu là ba đường cổng này từng
    // báo oan "nút chưa nối logic" cho một POC làm đúng; test cuối giữ lại tín hiệu thật.

    [Fact]
    public async Task UatDrive_OpensScreen_ThroughMenuItemInsideCollapsedGroup()
    {
        // Mục menu là <div class="nav-item"> nên không nằm trong tập button/a/.btn, và mục của nhóm chưa
        // xổ còn không có cả innerText — bước "Mở màn hình X" vì thế từng không khớp được gì.
        var html = RealShellPoc(
            ["Nhân viên", "Quản lý"],
            [
                new PocNavItem { Label = "Đơn của tôi" },
                new PocNavItem
                {
                    Label = "Danh mục",
                    Children =
                    [
                        new PocNavItem { Label = "Skill Catalog" },
                        new PocNavItem { Label = "Degree Catalog" }
                    ]
                }
            ],
            Screen("Đơn của tôi") + Screen("Skill Catalog") + Screen("Degree Catalog"));

        var report = await DriveAsync(html, Scenario("Xem danh mục kỹ năng", "Mở màn hình \"Skill Catalog\""));
        if (!report.Ran)
            return;

        var driven = Assert.Single(report.UatDriveResults);
        Assert.True(driven.Pass, driven.Detail);
    }

    [Fact]
    public async Task UatDrive_ScreenNamedAfterARole_DoesNotClickTheRoleButtonInstead()
    {
        // "HRBP Approval" chứa "HRBP": khi bước không khớp được mục menu, khớp NGƯỢC bấm trúng nút vai
        // HRBP — vai vừa chọn ở bước trước — rồi chấm cú no-op đó là nút chết. Đúng lớp báo oan đã đánh
        // trượt cả 5 kịch bản của một POC chạy được.
        var html = RealShellPoc(
            ["Manager", "HRBP"],
            [
                new PocNavItem { Label = "Đơn của tôi" },
                new PocNavItem { Label = "HRBP Approval", Roles = ["HRBP"] }
            ],
            Screen("Đơn của tôi") + Screen("HRBP Approval", "HRBP"));

        var report = await DriveAsync(html, Scenario(
            "HRBP duyệt JD",
            "Chọn vai \"HRBP\" ở khối VIEW AS",
            "Mở màn hình \"HRBP Approval\""));
        if (!report.Ran)
            return;

        var driven = Assert.Single(report.UatDriveResults);
        Assert.True(driven.Pass, driven.Detail);
        Assert.DoesNotContain(report.Issues, i => i.Contains("KHÔNG thao tác được"));
    }

    [Fact]
    public async Task UatDrive_ReSelectingTheRoleTheDemoOpensWith_IsNotADeadButton()
    {
        // Vai ĐẦU TIÊN là vai demo mở lên, nên bước 1 "chọn vai Manager" bấm lại đúng nút đang active:
        // không đổi màn, không đổi vai — no-op ĐÚNG, không phải nút chưa nối logic.
        var html = RealShellPoc(
            ["Manager", "HRBP"],
            [new PocNavItem { Label = "Đơn của tôi" }, new PocNavItem { Label = "Duyệt đơn" }],
            Screen("Đơn của tôi") + Screen("Duyệt đơn"));

        var report = await DriveAsync(html, Scenario(
            "Manager gửi JD",
            "Chọn vai \"Manager\" ở khối VIEW AS",
            "Mở màn hình \"Duyệt đơn\""));
        if (!report.Ran)
            return;

        var driven = Assert.Single(report.UatDriveResults);
        Assert.True(driven.Pass, driven.Detail);
    }

    [Fact]
    public async Task UatDrive_ButtonWithNoLogic_IsStillReported()
    {
        // Rào chắn cho ba test trên: nới tập điều khiển và thêm nhánh bỏ qua KHÔNG được làm cổng này
        // im lặng trước cái nó sinh ra để bắt — nút có nhãn, bấm được, mà màn hình đứng yên.
        var html = RealShellPoc(
            ["Nhân viên", "Quản lý"],
            [new PocNavItem { Label = "Đơn của tôi" }],
            Screen("Đơn của tôi"));

        var report = await DriveAsync(html, Scenario("Gửi đơn", "Bấm \"Gửi\" để nộp đơn"));
        if (!report.Ran)
            return;

        var driven = Assert.Single(report.UatDriveResults);
        Assert.False(driven.Pass);
        Assert.Contains("chưa nối logic", driven.Detail);
        Assert.Contains(report.Issues, i => i.Contains("KHÔNG thao tác được") && i.Contains("Gửi đơn"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _checker.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
