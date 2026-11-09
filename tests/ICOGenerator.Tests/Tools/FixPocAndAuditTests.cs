using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Tools;

// Các tool gộp bước của đường agent dựng POC: một vòng sửa = MỘT call FixPocAndAudit (nhiều chỗ sửa + nối
// thêm + tự audit lại), và call script cuối tự audit (audit: true) — để agent loop không tốn một lượt (gửi
// lại cả lịch sử) cho mỗi ReplaceInFile lẻ hay cho riêng lời gọi AuditPocContent.
public sealed class FixPocAndAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "poc-fix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceTools _tools;
    private readonly string _mockup;

    public FixPocAndAuditTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentWorkspace:RootPath"] = _root,
                ["Poc:VisualCheck:Enabled"] = "false"
            })
            .Build();
        _tools = new WorkspaceTools(configuration, new WorkspacePathResolver(configuration), new SkippedRuntime(),
            new PocVisualReviewer(null!, null!, null!, configuration, NullLogger<PocVisualReviewer>.Instance));
        _tools.SetWorkspacePath(Path.Combine(_root, "ws"));
        _mockup = _tools.GetSafeFullPath(PocTemplate.MockupRelativePath);
        Assert.True(PocDemoSeeder.SeedAsync(PocDemoSeeder.TemplatePath(RepoRoot()), Path.GetDirectoryName(_mockup)!).GetAwaiter().GetResult());
    }

    private static string Section(string view, string body, bool active = false) =>
        $"<section class=\"page-view{(active ? " active" : "")}\" data-view=\"{view}\"><div class=\"card\"><div class=\"card-body\">{body}</div></div></section>";

    private async Task SeedDemoAsync()
    {
        var reply = await _tools.SetPocContent(
            Section("Dashboard", "Tổng đơn: <span id=\"total\">0</span>", active: true),
            "Leave Desk", "Home > Dashboard",
            System.Text.Json.JsonDocument.Parse("""[{"label":"Dashboard","icon":"house"},{"label":"Requests","icon":"list-ul"}]""").RootElement);
        Assert.StartsWith("POC content updated", reply);
        await _tools.SetPocScript("function refreshTotals(){ return 1; }");
    }

    [Fact]
    public async Task AppliesEveryEditAndAppend_ThenReturnsTheAuditReport_InOneCall()
    {
        await SeedDemoAsync();

        var reply = await _tools.FixPocAndAudit(
            [
                new PocEdit("Tổng đơn:", "Tổng số đơn:"),
                new PocEdit("return 1;", "return 2;")
            ],
            appendContent: Section("Requests", "Danh sách đơn"),
            appendScript: "function pocSelfTest(){ return []; }");

        Assert.Contains("2/2 edit(s) applied", reply);
        Assert.Contains("content appended", reply);
        Assert.Contains("script appended", reply);
        Assert.Contains("RUNTIME CHECK", reply); // báo cáo audit đi kèm, không cần lượt AuditPocContent riêng

        var html = await File.ReadAllTextAsync(_mockup);
        Assert.Contains("Tổng số đơn:", PocTemplate.GetContentBody(html));
        Assert.Contains("data-view=\"Requests\"", PocTemplate.GetContentBody(html));
        var script = PocTemplate.GetScriptBody(html);
        Assert.Contains("return 2;", script);
        Assert.Contains("function pocSelfTest", script);
    }

    // Một chỗ sửa khớp 0 hoặc nhiều chỗ bị bỏ và BÁO LẠI; các chỗ khác vẫn được áp.
    [Fact]
    public async Task AnAmbiguousOrMissingEdit_IsReported_WithoutBlockingTheOthers()
    {
        await SeedDemoAsync();

        var reply = await _tools.FixPocAndAudit(
        [
            new PocEdit("Tổng đơn:", "Tổng số đơn:"),
            new PocEdit("không có đoạn này", "x"),
            new PocEdit("card", "panel") // khớp nhiều chỗ
        ]);

        Assert.Contains("1/3 edit(s) applied", reply);
        Assert.Contains("NOT applied", reply);
        Assert.Contains("Không tìm thấy", reply);
        Assert.Contains("khớp", reply);
        Assert.Contains("Tổng số đơn:", await File.ReadAllTextAsync(_mockup));
    }

    // Không áp được gì ⇒ không ghi file, không tốn một lần audit (headless browser) cho một bản demo y nguyên.
    [Fact]
    public async Task NothingApplied_SkipsTheAudit_AndLeavesTheFileUntouched()
    {
        await SeedDemoAsync();
        var before = await File.ReadAllTextAsync(_mockup);

        var reply = await _tools.FixPocAndAudit([new PocEdit("không có đoạn này", "x")]);

        Assert.StartsWith("Nothing was applied", reply);
        Assert.DoesNotContain("RUNTIME CHECK", reply);
        Assert.Equal(before, await File.ReadAllTextAsync(_mockup));
    }

    // Shell (sidebar, script shell) không nằm trong vùng được tìm: model không được xem nó, khớp ở đó chỉ là khớp nhầm.
    [Fact]
    public async Task EditsNeverTouchTheShell()
    {
        await SeedDemoAsync();
        var html = await File.ReadAllTextAsync(_mockup);
        var shellOnly = "<head>";
        Assert.Contains(shellOnly, html);

        var reply = await _tools.FixPocAndAudit([new PocEdit(shellOnly, "<head data-x>")]);

        Assert.StartsWith("Nothing was applied", reply);
        Assert.DoesNotContain("<head data-x>", await File.ReadAllTextAsync(_mockup));
    }

    [Fact]
    public async Task ScriptCalls_AuditOnlyWhenAsked()
    {
        await SeedDemoAsync();

        var plain = await _tools.SetPocScript("function a(){}");
        Assert.DoesNotContain("RUNTIME CHECK", plain);

        var audited = await _tools.AppendPocScript("function b(){}", audit: true);
        Assert.StartsWith("POC script appended", audited);
        Assert.Contains("RUNTIME CHECK", audited);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ICOGenerator.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Không tìm thấy repo root từ " + AppContext.BaseDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class SkippedRuntime : IPocRuntimeChecker
    {
        public Task<PocRuntimeReport> CheckAsync(string pocHtmlPath, bool captureScreenshots = false,
            IReadOnlyList<UatScenario>? uatScenarios = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(PocRuntimeReport.Skipped("tắt trong test"));
    }
}
