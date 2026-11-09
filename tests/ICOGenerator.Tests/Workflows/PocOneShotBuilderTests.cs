using System.Runtime.CompilerServices;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

// Đường dựng POC một lần: lượt SINH ghi bản demo qua chính các tool POC, CODE tự chạy AuditPocContent, lượt
// SỬA chỉ chạy khi audit còn mục chưa đạt — và mỗi lượt là một lời gọi độc lập (không mang lịch sử). Chạy
// trên template thật (Prompts/Design/poc-template.html); tầng runtime/visual được tắt để test không cần
// Chromium hay model vision — phần audit tĩnh (menu ↔ section…) vẫn chạy thật.
public sealed class PocOneShotBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "poc-oneshot-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _configuration;
    private readonly WorkspaceTools _tools;
    private readonly string _mockup;

    public PocOneShotBuilderTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentWorkspace:RootPath"] = _root,
                ["Poc:VisualCheck:Enabled"] = "false"
            })
            .Build();
        var resolver = new WorkspacePathResolver(_configuration);
        _tools = new WorkspaceTools(_configuration, resolver, new SkippedRuntime(),
            new PocVisualReviewer(null!, null!, null!, _configuration, NullLogger<PocVisualReviewer>.Instance));
        _tools.SetWorkspacePath(Path.Combine(_root, "ws"));
        _mockup = _tools.GetSafeFullPath(PocTemplate.MockupRelativePath);
        Assert.True(PocDemoSeeder.SeedAsync(PocDemoSeeder.TemplatePath(RepoRoot()), Path.GetDirectoryName(_mockup)!).GetAwaiter().GetResult());
    }

    private const string Meta = """{"appName":"Leave Desk","breadcrumb":"Home > Dashboard","navItems":[{"label":"Dashboard","icon":"house"},{"label":"Requests","icon":"list-ul"}]}""";

    private static string Section(string view, bool active = false) =>
        $"<section class=\"page-view{(active ? " active" : "")}\" data-view=\"{view}\"><div class=\"card\"><div class=\"card-body\">{view}</div></div></section>";

    private static string Build(string content) =>
        "===POC_META===\n" + Meta + "\n===POC_CONTENT===\n" + content +
        "\n===POC_SCRIPT===\nfunction refreshTotals(){ return 1; }\n===POC_HANDOFF===\n- Bàn giao của model.\n===POC_END===";

    [Fact]
    public async Task CleanBuild_IsOneCall_AndWritesTheDemo()
    {
        var chat = new ScriptedChat(Build(Section("Dashboard", active: true) + Section("Requests")));

        var outcome = await NewBuilder(chat, maxRepairRounds: 2).TryBuildAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Built, outcome.FallbackReason);
        Assert.Equal(1, chat.Calls);
        var html = await File.ReadAllTextAsync(_mockup);
        Assert.Contains("data-view=\"Requests\"", html);
        Assert.Contains("Leave Desk", html);
        Assert.Contains("function refreshTotals", PocTemplate.GetScriptBody(html));
        Assert.Contains("Bàn giao của model.", outcome.Output);
        Assert.Contains("Tự kiểm cuối: sạch", outcome.Output);

        // Prompt bước đứng làm system (để lượt sửa đọc lại được từ cache), chỉ dẫn cách giao đứng ở user.
        Assert.Equal("PROMPT BƯỚC POC", chat.Requests[0].Single(m => m.Role == ChatRole.System).Text);
        Assert.Contains("===POC_END===", chat.Requests[0].Single(m => m.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task AuditIssues_TriggerARepairCall_ThatSeesTheCurrentDemoAndTheReport()
    {
        var chat = new ScriptedChat(
            Build(Section("Dashboard", active: true)), // menu có "Requests" mà chưa có section ⇒ ISSUE
            "===POC_APPEND_CONTENT===\n" + Section("Requests") + "\n===POC_NOTES===\n- Thêm màn Requests.\n===POC_END===");

        var outcome = await NewBuilder(chat, maxRepairRounds: 2).TryBuildAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Built);
        Assert.Equal(2, chat.Calls);

        var repairPrompt = chat.Requests[1].Single(m => m.Role == ChatRole.User).Text;
        Assert.Contains("# ĐẦU VÀO: Báo cáo AuditPocContent", repairPrompt);
        Assert.Contains("Requests", repairPrompt);
        Assert.Contains("data-view=\"Dashboard\"", repairPrompt); // bản demo hiện tại đi kèm, không cần ReadFile
        // Lượt sửa là lời gọi ĐỘC LẬP: không mang câu trả lời của lượt sinh làm lịch sử.
        Assert.DoesNotContain(chat.Requests[1], m => m.Role == ChatRole.Assistant);

        Assert.Contains("data-view=\"Requests\"", await File.ReadAllTextAsync(_mockup));
        Assert.Contains("Tự kiểm cuối: sạch", outcome.Output);
        Assert.Contains("Thêm màn Requests.", outcome.Output);
    }

    [Fact]
    public async Task NoRepairRounds_LeavesTheRemainingIssuesInTheHandoff()
    {
        var chat = new ScriptedChat(Build(Section("Dashboard", active: true)));

        var outcome = await NewBuilder(chat, maxRepairRounds: 0).TryBuildAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Built);
        Assert.Equal(1, chat.Calls);
        Assert.Contains("Tự kiểm cuối: còn", outcome.Output);
    }

    // Câu trả lời bị cắt: caller phải đi đường agent, và bản demo seed CHƯA bị đụng tới.
    [Fact]
    public async Task TruncatedBuild_FallsBack_WithoutTouchingTheDemo()
    {
        var before = await File.ReadAllTextAsync(_mockup);
        var chat = new ScriptedChat("===POC_META===\n" + Meta + "\n===POC_CONTENT===\n" + Section("Dashboard"));

        var outcome = await NewBuilder(chat, maxRepairRounds: 2).TryBuildAsync(Request(), CancellationToken.None);

        Assert.False(outcome.Built);
        Assert.Contains("POC_END", outcome.FallbackReason);
        Assert.Equal(before, await File.ReadAllTextAsync(_mockup));
    }

    // Lượt sửa làm TĂNG số mục còn lại ⇒ giữ lại bản tốt nhất (bản trước lượt sửa).
    [Fact]
    public async Task ARepairThatMakesThingsWorse_IsRolledBack()
    {
        var chat = new ScriptedChat(
            Build(Section("Dashboard", active: true)),
            // Đổi tên màn đang có ⇒ cả hai mục menu đều mất section.
            "===POC_FIND===\ndata-view=\"Dashboard\"\n===POC_REPLACE===\ndata-view=\"Home\"\n===POC_END===");

        var outcome = await NewBuilder(chat, maxRepairRounds: 1).TryBuildAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Built);
        Assert.Contains("data-view=\"Dashboard\"", await File.ReadAllTextAsync(_mockup));
        Assert.Contains("giữ lại bản tốt nhất", outcome.Output);
    }

    // Câu trả lời dài hơn một đoạn: lượt sinh dừng ở trần token (không phải lỗi), model được đòi VIẾT TIẾP với
    // phần đã viết làm lượt của chính nó — và phần nối lại được ghép thành một câu trả lời trọn vẹn.
    [Fact]
    public async Task ALongReply_IsContinued_AcrossSegments_InsteadOfFallingBack()
    {
        var full = Build(Section("Dashboard", active: true) + Section("Requests"));
        var split = full.IndexOf("===POC_SCRIPT===", StringComparison.Ordinal) - 30;
        var chat = new ScriptedChat(Cut + full[..split], full[(split - 25)..]); // đoạn 2 chép lại 25 ký tự cuối

        var outcome = await NewBuilder(chat, maxRepairRounds: 0).TryBuildAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Built, outcome.FallbackReason);
        Assert.Equal(2, chat.Calls);
        Assert.All(chat.MaxOutputTokens, t => Assert.Equal(PocOneShotBuilder.SegmentOutputTokens, t));

        var continuation = chat.Requests[1];
        Assert.Equal(full[..split], continuation.Single(m => m.Role == ChatRole.Assistant).Text);
        Assert.Contains("Viết TIẾP", continuation.Last().Text);

        var html = await File.ReadAllTextAsync(_mockup);
        Assert.Contains("data-view=\"Requests\"", html);
        Assert.Contains("function refreshTotals", PocTemplate.GetScriptBody(html));
        Assert.Contains("Tự kiểm cuối: sạch", outcome.Output);
    }

    // Dùng hết trần mà KHÔNG ra chữ nào (model reasoning nghĩ hết hạn mức): không gọi lại y nguyên — nới trần.
    [Fact]
    public async Task AnEmptyCutReply_RaisesTheCap_InsteadOfRepeatingTheSameCall()
    {
        var chat = new ScriptedChat(Cut, Build(Section("Dashboard", active: true) + Section("Requests")));

        var outcome = await NewBuilder(chat, maxRepairRounds: 0).TryBuildAsync(Request(), CancellationToken.None);

        Assert.True(outcome.Built, outcome.FallbackReason);
        Assert.Equal(new int?[] { PocOneShotBuilder.SegmentOutputTokens, Math.Min(PocOneShotBuilder.SegmentOutputTokens * 2, PocOneShotBuilder.MaxSegmentOutputTokens) }, chat.MaxOutputTokens);
        Assert.DoesNotContain(chat.Requests[1], m => m.Role == ChatRole.Assistant); // không có gì để viết tiếp
    }

    [Fact]
    public async Task EmptyCutReplies_StopAtTheMaxCap_AndFallBack()
    {
        var before = await File.ReadAllTextAsync(_mockup);
        var chat = new ScriptedChat(Cut, Cut, Cut, Cut, Cut);

        var outcome = await NewBuilder(chat, maxRepairRounds: 0).TryBuildAsync(Request(), CancellationToken.None);

        Assert.False(outcome.Built);
        Assert.Contains("suy luận ẩn", outcome.FallbackReason);
        Assert.Equal(new int?[] { 60_000, PocOneShotBuilder.MaxSegmentOutputTokens }, chat.MaxOutputTokens);
        Assert.Equal(before, await File.ReadAllTextAsync(_mockup));
    }

    [Fact]
    public void Stitch_DropsARepeatedTail_AndKeepsMarkersOnTheirOwnLine()
    {
        var reply = new System.Text.StringBuilder("<section>abc</section>\n<div class=\"card\">mot hai ba");
        PocOneShotBuilder.Stitch(reply, "<div class=\"card\">mot hai ba bon</div>");
        Assert.Equal("<section>abc</section>\n<div class=\"card\">mot hai ba bon</div>", reply.ToString());

        PocOneShotBuilder.Stitch(reply, "===POC_SCRIPT===\nvar a;");
        Assert.EndsWith("bon</div>\n===POC_SCRIPT===\nvar a;", reply.ToString());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private PocOneShotBuilder NewBuilder(ScriptedChat chat, int maxRepairRounds)
    {
        var env = new FakeWebHostEnvironment { ContentRootPath = RepoRoot() };
        var llm = new LlmClient(new SingleClientFactory(chat), new NoopCallLogger(), new NoBudgetLimit(), new LlmSettings(), NullLogger<LlmClient>.Instance);
        return new PocOneShotBuilder(llm, new PromptTemplateService(env), _tools,
            NullLogger<PocOneShotBuilder>.Instance, maxRepairRounds);
    }

    private static PocOneShotRequest Request() => new(
        Guid.NewGuid(),
        new Agent { RoleKey = AgentRoleKey.Developer },
        new AiModel { ModelId = "test-model" },
        "PROMPT BƯỚC POC",
        null,
        PocOneShotBuilder.BuildPurpose,
        PocOneShotBuilder.RepairPurpose,
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

    // Câu trả lời bắt đầu bằng Cut ⇒ lượt đó dừng vì chạm trần token (finish_reason=length).
    private const string Cut = "[[LENGTH]]";

    private sealed class ScriptedChat : IChatClient
    {
        private readonly Queue<string> _replies;
        public List<List<ChatMessage>> Requests { get; } = new();
        public List<int?> MaxOutputTokens { get; } = new();
        public int Calls => Requests.Count;

        public ScriptedChat(params string[] replies) => _replies = new Queue<string>(replies);

        private string Next(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            Requests.Add(messages.ToList());
            MaxOutputTokens.Add(options?.MaxOutputTokens);
            return _replies.Count > 0 ? _replies.Dequeue() : "===POC_NOTES===\n-\n===POC_END===";
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Next(messages, options))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            var text = Next(messages, options);
            var cut = text.StartsWith(Cut, StringComparison.Ordinal);
            yield return new ChatResponseUpdate(ChatRole.Assistant, cut ? text[Cut.Length..] : text)
            {
                FinishReason = cut ? ChatFinishReason.Length : ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SingleClientFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient Create(AiModel model) => client;
    }

    private sealed class NoopCallLogger : IModelCallLogger
    {
        public Task LogAsync(Guid projectId, Agent agent, LlmCallResult callResult, int step, string purpose, Guid? workflowRunId = null) => Task.CompletedTask;
    }

    private sealed class NoBudgetLimit : IBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
