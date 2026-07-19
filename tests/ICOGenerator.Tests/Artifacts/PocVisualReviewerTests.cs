using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Tầng Visual QA: agent UI/UX (vision) chấm ảnh POC. Fail-open khi chưa cấu hình agent/model vision;
// có cấu hình thì trả issues/warnings có gắn tên màn hình cho Developer sửa.
public class PocVisualReviewerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public PocVisualReviewerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });
        db.SaveChanges();
    }

    private static readonly IReadOnlyList<PocScreenshot> Shots = new[]
    {
        new PocScreenshot("Trang chủ", new byte[] { 1, 2, 3 })
    };

    [Fact]
    public async Task NoUiUxAgent_IsSkipped()
    {
        await using var db = NewDb();
        var sut = NewSut(db, new FakeLlm());

        Assert.False(await sut.IsConfiguredAsync());
        var report = await sut.ReviewAsync(_projectId, "spec", Shots);
        Assert.False(report.Ran);
    }

    [Fact]
    public async Task UiUxAgentWithoutVision_IsSkipped()
    {
        await SeedDesignerAsync(supportsVision: false);
        await using var db = NewDb();
        var sut = NewSut(db, new FakeLlm());

        Assert.False(await sut.IsConfiguredAsync());
        Assert.False((await sut.ReviewAsync(_projectId, "spec", Shots)).Ran);
    }

    [Fact]
    public async Task NoScreenshots_IsSkipped()
    {
        await SeedDesignerAsync(supportsVision: true);
        await using var db = NewDb();
        var sut = NewSut(db, new FakeLlm());

        var report = await sut.ReviewAsync(_projectId, "spec", Array.Empty<PocScreenshot>());
        Assert.False(report.Ran);
    }

    [Fact]
    public async Task VisionAgent_ReturnsIssuesTaggedWithScreen()
    {
        await SeedDesignerAsync(supportsVision: true);
        var llm = new FakeLlm
        {
            Result = new PocVisualReviewResult
            {
                Issues = { new PocVisualFinding { Screen = "Danh sách", Detail = "Bảng trống." } },
                Warnings = { new PocVisualFinding { Screen = "", Detail = "Canh lề lệch." } }
            }
        };

        await using var db = NewDb();
        var report = await NewSut(db, llm).ReviewAsync(_projectId, "spec", Shots);

        Assert.True(report.Ran);
        Assert.Equal("[Danh sách] Bảng trống.", Assert.Single(report.Issues));
        Assert.Equal("Canh lề lệch.", Assert.Single(report.Warnings));
    }

    private async Task SeedDesignerAsync(bool supportsVision)
    {
        await using var db = NewDb();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "vision-model", SupportsVision = supportsVision };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.UiUx, Temperature = 0.2, AiModelId = model.Id });
        await db.SaveChangesAsync();
    }

    private static PocVisualReviewer NewSut(AppDbContext db, ILlmClient llm) =>
        new(db, llm, new StubPrompts(), new ConfigurationBuilder().Build(), NullLogger<PocVisualReviewer>.Instance);

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeLlm : ILlmClient
    {
        public PocVisualReviewResult? Result = new();

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
            => Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)(object?)Result));
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
