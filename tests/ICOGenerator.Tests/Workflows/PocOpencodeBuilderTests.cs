using System.Text.Json;
using ICOGenerator.Configuration;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

// Vỏ opencode của lượt "Thử model khác": opencode (ở đây là bản giả) giao bốn file nguồn, CODE ghép chúng vào
// bản demo qua chính các tool POC, tự kiểm bằng AuditPocContent (phần tĩnh chạy thật trên template thật) và
// gọi lại opencode với báo cáo khi còn mục chưa đạt — cùng nhịp với PocOneShotBuilder.
public sealed class PocOpencodeBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "poc-opencode-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceTools _tools;
    private readonly string _sandbox;
    private readonly string _mockup;

    public PocOpencodeBuilderTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentWorkspace:RootPath"] = _root,
                ["Poc:VisualCheck:Enabled"] = "false"
            })
            .Build();
        var resolver = new WorkspacePathResolver(configuration);
        _tools = new WorkspaceTools(configuration, resolver, new SkippedRuntime(),
            new PocVisualReviewer(null!, null!, null!, configuration, NullLogger<PocVisualReviewer>.Instance));
        _sandbox = Path.Combine(_root, ".poc-variants", "p", "v");
        _tools.SetWorkspacePath(_sandbox);
        _mockup = _tools.GetSafeFullPath(PocTemplate.MockupRelativePath);
        Assert.True(PocDemoSeeder.SeedAsync(PocDemoSeeder.TemplatePath(RepoRoot()), Path.GetDirectoryName(_mockup)!).GetAwaiter().GetResult());
    }

    private const string Meta = """{"appName":"Leave Desk","breadcrumb":"Home > Dashboard","navItems":[{"label":"Dashboard","icon":"house"},{"label":"Requests","icon":"list-ul"}]}""";

    private static string Section(string view, bool active = false) =>
        $"<section class=\"page-view{(active ? " active" : "")}\" data-view=\"{view}\"><div class=\"card\"><div class=\"card-body\">{view}</div></div></section>";

    [Fact]
    public async Task AuditIssues_RunOpencodeAgain_WithTheReport_AndTheDemoIsRebuiltFromTheSources()
    {
        var cli = new ScriptedOpencode(
            // Lượt dựng: menu có "Requests" mà chưa có section ⇒ ISSUE; còn lén sửa poc-demo.html.
            dir =>
            {
                WriteSources(dir, Section("Dashboard", active: true));
                File.AppendAllText(Path.Combine(dir, PocTemplate.MockupRelativePath), "<!-- opencode sửa thẳng file demo -->");
            },
            // Lượt sửa: thêm màn còn thiếu.
            dir => WriteSources(dir, Section("Dashboard", active: true) + Section("Requests"), handoff: "- Thêm màn Requests."));

        var output = await NewBuilder(cli).BuildAsync(Request(), CancellationToken.None);

        Assert.Equal(2, cli.Runs.Count);
        var repairBrief = cli.RunFiles[1];
        Assert.Contains("# ĐẦU VÀO: Báo cáo AuditPocContent", repairBrief);
        Assert.Contains("Requests", repairBrief);

        var html = await File.ReadAllTextAsync(_mockup);
        Assert.Contains("data-view=\"Requests\"", html);
        // Bản demo luôn được dựng lại từ template sạch — sửa thẳng của opencode không lọt vào bản được chấm.
        Assert.DoesNotContain("opencode sửa thẳng file demo", html);
        Assert.Contains("Tự kiểm cuối: sạch", output);
        Assert.Contains("Thêm màn Requests.", output);
        Assert.Contains("Cách dựng: opencode — 2 lần chạy (1 lượt dựng + 1 lượt sửa theo tự kiểm)", output);
    }

    [Fact]
    public async Task ARepairRunThatChangesNothing_StopsTheLoop()
    {
        var cli = new ScriptedOpencode(dir => WriteSources(dir, Section("Dashboard", active: true)), _ => { });

        var output = await NewBuilder(cli).BuildAsync(Request(), CancellationToken.None);

        Assert.Equal(2, cli.Runs.Count);
        Assert.Contains("không đổi file nguồn nào", output);
        Assert.Contains("Tự kiểm cuối: còn", output);
    }

    [Fact]
    public async Task AFailedBuildRun_FailsWithTheProcessOutput()
    {
        var cli = new ScriptedOpencode(_ => { }) { ExitCode = 1, Tail = "ProviderInitError: cannot reach relay" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBuilder(cli).BuildAsync(Request(), CancellationToken.None));

        Assert.Contains("mã 1", ex.Message);
        Assert.Contains("cannot reach relay", ex.Message);
    }

    // Lệnh chạy chỉ mang một câu ASCII cố định (trên Windows đối số đi qua cmd.exe); cấu hình chỉ mở `node`
    // cho bash, cấm web, trỏ provider về relay và đọc token từ biến môi trường.
    [Fact]
    public async Task TheCommandLineIsFixedAscii_AndTheConfigIsNarrow()
    {
        var cli = new ScriptedOpencode(dir => WriteSources(dir, Section("Dashboard", active: true) + Section("Requests")));

        await NewBuilder(cli).BuildAsync(Request(), CancellationToken.None);

        var run = cli.Runs[0];
        Assert.All(run.Arguments, a => Assert.True(a.All(c => c < 128), a));
        Assert.Equal(new[] { "run", "--model", "icogen/poc" }, run.Arguments.Take(3));
        Assert.StartsWith("icorelay_", run.Environment[PocOpencodeBuilder.TokenVariable]);
        // Token được che trong mọi output của tiến trình (client HTTP in cả header Authorization khi lỗi).
        Assert.Equal(new[] { run.Environment[PocOpencodeBuilder.TokenVariable] }, run.Secrets);
        Assert.Equal("Authorization: Bearer ***", PocOpencodeCli.Redact("Authorization: Bearer " + run.Secrets![0], run.Secrets));
        Assert.EndsWith(Path.Combine(".poc-variants", ".opencode-home", "config"), run.Environment["XDG_CONFIG_HOME"]);

        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_sandbox, PocOpencodeBuilder.ConfigFile)));
        var permission = config.RootElement.GetProperty("permission");
        Assert.Equal("deny", permission.GetProperty("webfetch").GetString());
        Assert.Equal("deny", permission.GetProperty("bash").GetProperty("*").GetString());
        Assert.Equal("allow", permission.GetProperty("bash").GetProperty("node *").GetString());
        var options = config.RootElement.GetProperty("provider").GetProperty("icogen").GetProperty("options");
        Assert.Equal("http://127.0.0.1:5999/opencode-relay/v1", options.GetProperty("baseURL").GetString());
        Assert.Equal("{env:ICOGEN_RELAY_TOKEN}", options.GetProperty("apiKey").GetString());
    }

    [Fact]
    public void RelayAddress_PrefersConfig_ThenMapsAWildcardKestrelAddressToLoopback()
    {
        Assert.Equal("http://app:8080", new PocOpencodeRelayAddress(new PocOpencodeSettings { RelayBaseUrl = "http://app:8080/" }).Resolve());
        Assert.Null(new PocOpencodeRelayAddress(new PocOpencodeSettings()).Resolve());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static void WriteSources(string sandbox, string content, string handoff = "- Bàn giao.")
    {
        var src = Path.Combine(sandbox, PocOpencodeBuilder.SourceFolder);
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, PocOpencodeBuilder.MetaFile), Meta);
        File.WriteAllText(Path.Combine(src, PocOpencodeBuilder.ContentFile), content);
        File.WriteAllText(Path.Combine(src, PocOpencodeBuilder.ScriptFile), "function refreshTotals(){ return 1; }");
        File.WriteAllText(Path.Combine(src, PocOpencodeBuilder.HandoffFile), handoff);
    }

    private PocOpencodeBuilder NewBuilder(IPocOpencodeCli cli)
    {
        var env = new FakeWebHostEnvironment { ContentRootPath = RepoRoot() };
        var settings = new PocOpencodeSettings { Enabled = true, RelayBaseUrl = "http://127.0.0.1:5999" };
        return new PocOpencodeBuilder(_tools, new PromptTemplateService(env), new LlmRelaySessions(), cli,
            new PocOpencodeRelayAddress(settings), settings, NullLogger<PocOpencodeBuilder>.Instance);
    }

    private static PocOpencodeRequest Request() => new(
        Guid.NewGuid(),
        new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.Developer },
        new AiModel { Id = Guid.NewGuid(), ModelId = "test-model" },
        "PROMPT BƯỚC POC",
        "PocVariant",
        null);

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

    // Mỗi lần chạy làm một việc với thư mục thử (theo thứ tự); hết kịch bản thì không làm gì.
    private sealed class ScriptedOpencode(params Action<string>[] steps) : IPocOpencodeCli
    {
        public List<PocOpencodeRun> Runs { get; } = new();
        public List<string> RunFiles { get; } = new();
        public int ExitCode { get; init; }
        public string Tail { get; init; } = string.Empty;

        public Task<PocOpencodeRunResult> RunAsync(PocOpencodeRun run, CancellationToken cancellationToken)
        {
            Runs.Add(run);
            RunFiles.Add(File.ReadAllText(Path.Combine(run.WorkingDirectory, PocOpencodeBuilder.RunFile)));
            if (Runs.Count <= steps.Length)
                steps[Runs.Count - 1](run.WorkingDirectory);
            return Task.FromResult(new PocOpencodeRunResult(ExitCode, false, Tail));
        }
    }

    private sealed class SkippedRuntime : IPocRuntimeChecker
    {
        public Task<PocRuntimeReport> CheckAsync(string pocHtmlPath, bool captureScreenshots = false,
            IReadOnlyList<UatScenario>? uatScenarios = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(PocRuntimeReport.Skipped("tắt trong test"));
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }
}
