using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace ICOGenerator.Tests.Tools;

// Tầng Visual QA (agent UI/UX chấm ảnh mọi màn hình) là lời gọi model đắt nhất của một vòng audit POC. Ca JD
// Library 2026-11-26: 4 lần chấm cho một bản demo, hai lần liền nhau cách chưa tới một phút trên cùng file.
// Các test chốt: chấm lại một bản demo y nguyên thì DÙNG LẠI kết quả; demo còn lỗi chạy/kịch bản gãy/thiếu màn
// thì HOÃN (vòng sửa sau sẽ đổi chính các màn đó); issue tĩnh không hoãn.
public class PocVisualReviewPlanTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ico-visual-plan-" + Guid.NewGuid().ToString("N"));

    public PocVisualReviewPlanTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = NewDb();
        db.Database.EnsureCreated();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "vision", SupportsVision = true };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = Guid.NewGuid(), AiModelId = model.Id, RoleKey = AgentRoleKey.UiUx });
        db.SaveChanges();
    }

    // ── Quyết định thuần ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unchanged_demo_reuses_the_previous_review()
    {
        var previous = new PocVerificationSummary { VisualRan = true, VisualContentHash = "H" };

        Assert.Equal(WorkspaceTools.VisualPlan.Reuse, WorkspaceTools.PlanVisualReview(previous, "H", 0, 0, 3, 3));
        // Đã chấm mà nay còn lỗi chạy: bản demo không đổi thì kết quả cũ vẫn đúng — dùng lại, không hoãn.
        Assert.Equal(WorkspaceTools.VisualPlan.Reuse, WorkspaceTools.PlanVisualReview(previous, "H", 2, 0, 3, 3));
    }

    [Theory]
    [InlineData(1, 0, 3, 3)] // lỗi JS / màn không mở được
    [InlineData(0, 1, 3, 3)] // kịch bản nghiệm thu gãy
    [InlineData(0, 0, 3, 2)] // thiếu màn hình của spec
    public void Broken_demo_defers_the_review(int runtimeIssues, int scenarioIssues, int specScreens, int coveredScreens)
    {
        Assert.Equal(WorkspaceTools.VisualPlan.Defer,
            WorkspaceTools.PlanVisualReview(null, "H", runtimeIssues, scenarioIssues, specScreens, coveredScreens));
    }

    [Fact]
    public void Changed_clean_demo_is_reviewed()
    {
        var previous = new PocVerificationSummary { VisualRan = true, VisualContentHash = "CŨ" };

        Assert.Equal(WorkspaceTools.VisualPlan.Run, WorkspaceTools.PlanVisualReview(previous, "MỚI", 0, 0, 3, 3));
        Assert.Equal(WorkspaceTools.VisualPlan.Run, WorkspaceTools.PlanVisualReview(null, "MỚI", 0, 0, 0, 0));
    }

    // ── Qua vòng audit thật (runtime + reviewer giả) ─────────────────────────────────────────────────

    [Fact]
    public async Task Auditing_an_unchanged_demo_twice_reviews_screenshots_once_and_keeps_the_findings()
    {
        var llm = new CountingLlm();
        WritePoc("<p>Danh sách kho</p>");

        await using var db = NewDb();
        var tools = NewTools(db, llm, new FakeRuntime());
        var first = await tools.AuditPocContent();
        var second = await tools.AuditPocContent();

        Assert.Equal(1, llm.Calls);
        Assert.Contains("Bảng tràn khỏi khung", first);
        Assert.Contains("POC unchanged since the last screenshot review", second);
        Assert.Contains("Bảng tràn khỏi khung", second);
        var saved = PocVerification.TryLoad(Path.Combine(_root, "p"))!;
        Assert.True(saved.VisualRan);
        Assert.Contains(saved.OpenIssues, i => i.Contains("Bảng tràn khỏi khung"));
    }

    [Fact]
    public async Task Runtime_errors_defer_the_review_until_the_demo_runs_clean()
    {
        var llm = new CountingLlm();
        WritePoc("<p>Danh sách kho</p>");

        await using var db = NewDb();
        var report = await NewTools(db, llm, new FakeRuntime("TypeError: x is undefined")).AuditPocContent();

        Assert.Equal(0, llm.Calls);
        Assert.Contains("VISUAL CHECK: DEFERRED", report);
        Assert.False(PocVerification.TryLoad(Path.Combine(_root, "p"))!.VisualRan);

        // Demo đã sửa (nội dung đổi) và chạy sạch ⇒ chấm.
        WritePoc("<p>Danh sách kho — đã sửa</p>");
        await NewTools(db, llm, new FakeRuntime()).AuditPocContent();
        Assert.Equal(1, llm.Calls);
    }

    private void WritePoc(string content)
    {
        var dir = Path.Combine(_root, "p", "04_Implementation");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "poc-demo.html"),
            "<html><body><nav class=\"sidebar-nav\"></nav>"
            + PocTemplate.StartMarker
            + "<section class=\"page-view active\" data-view=\"Kho vật tư\">" + content + "</section>"
            + PocTemplate.EndMarker
            + "</body></html>");
    }

    private WorkspaceTools NewTools(AppDbContext db, ILlmClient llm, IPocRuntimeChecker runtime)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _root })
            .Build();
        var visual = new PocVisualReviewer(db, llm, new StubPrompts(), configuration, NullLogger<PocVisualReviewer>.Instance);
        var tools = new WorkspaceTools(configuration, new WorkspacePathResolver(configuration), runtime, visual);
        tools.SetWorkspace("p");
        return tools;
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeRuntime(params string[] issues) : IPocRuntimeChecker
    {
        public Task<PocRuntimeReport> CheckAsync(string pocHtmlPath, bool captureScreenshots = false,
            IReadOnlyList<UatScenario>? uatScenarios = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PocRuntimeReport(true, null, issues, Array.Empty<string>(),
                captureScreenshots ? [new PocScreenshot("Kho vật tư", [1, 2, 3])] : Array.Empty<PocScreenshot>(),
                Array.Empty<string>(), Array.Empty<PocUatDriveResult>(), Array.Empty<PocScreenTable>()));
    }

    private sealed class CountingLlm : ILlmClient
    {
        public int Calls;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            Calls++;
            var review = new PocVisualReviewResult
            {
                Issues = [new PocVisualFinding { Screen = "Kho vật tư", Detail = "Bảng tràn khỏi khung" }]
            };
            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)(object)review));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt";
    }
}
