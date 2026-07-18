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
    public async Task CaptureScreenshots_ReturnsOnePngPerOpenedScreen()
    {
        var path = Path.Combine(_dir, "poc-demo.html");
        await File.WriteAllTextAsync(path, Shell.Replace("{SCRIPT}", ""));

        var report = await _checker.CheckAsync(path, captureScreenshots: true);
        if (!report.Ran)
            return; // không có Chromium: fail-open, không có gì để assert.

        // Hai màn hình mở được (Trang chủ + Danh sách) ⇒ hai ảnh PNG không rỗng.
        Assert.Equal(2, report.Screenshots.Count);
        Assert.All(report.Screenshots, s => Assert.True(s.Png.Length > 0));
        Assert.Contains(report.Screenshots, s => s.Screen == "Trang chủ");
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

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _checker.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
