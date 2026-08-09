using ICOGenerator.Application.Requirements;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bước XEM TRƯỚC của lượt gửi ghi chú POC: máy đề xuất đường xử lý cho từng ghi chú, người dùng soát lại.
// Hai điều phải giữ: (1) hợp đồng index ↔ ghi chú không lệch, (2) mọi nhánh hỏng đều rơi về nhóm RẺ —
// không bao giờ tự đề xuất đường đắt (soạn lại tài liệu + dựng lại POC) bằng một kết quả phân loại
// mà chính mình không có.
public class TriagePocFeedbackUseCaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public TriagePocFeedbackUseCaseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Projects.Add(new Project { Id = _projectId, Name = "P" });
        db.SaveChanges();
    }

    [Fact]
    public async Task MapsVerdictsBackToCommentsByIndex_AndDoesNotChangeAnyStatus()
    {
        ConfigureBa();
        var first = AddComment("nhãn nút sai");
        var second = AddComment("thiếu bước duyệt");

        await using var db = NewDb();
        var report = await NewSut(db, new PocFeedbackTriageResult
        {
            Items =
            {
                new PocFeedbackTriageVerdict { Index = 1, IsRequirementIssue = false, Reason = "chỉ là nhãn" },
                new PocFeedbackTriageVerdict { Index = 2, IsRequirementIssue = true, Reason = "thiếu bước quy trình" }
            }
        }).ExecuteAsync(_projectId);

        Assert.Equal(PocFeedbackTriageStatus.Ok, report.Status);
        Assert.True(report.Classified);

        var byId = report.Items.ToDictionary(i => i.Id);
        Assert.False(byId[first].IsRequirementIssue);
        Assert.Equal("chỉ là nhãn", byId[first].Reason);
        Assert.True(byId[second].IsRequirementIssue);

        // Bước này chỉ đọc: trạng thái ghi chú phải nguyên vẹn cho tới khi người dùng bấm gửi.
        await using var verify = NewDb();
        Assert.True(await verify.PocComments.AllAsync(c => c.Status == PocCommentStatus.Open));
    }

    [Fact]
    public async Task ModelFailure_PutsEverythingInTheCheapGroup_AndFlagsUnclassified()
    {
        ConfigureBa();
        AddComment("một ghi chú");

        await using var db = NewDb();
        var report = await NewSut(db, triaged: null).ExecuteAsync(_projectId);

        Assert.Equal(PocFeedbackTriageStatus.Ok, report.Status);
        Assert.False(report.Classified);
        Assert.True(report.BaConfigured);
        Assert.All(report.Items, i => Assert.False(i.IsRequirementIssue));
    }

    [Fact]
    public async Task MissingVerdictForAComment_DefaultsToTheCheapGroup()
    {
        ConfigureBa();
        AddComment("có phán quyết");
        AddComment("model bỏ sót");

        await using var db = NewDb();
        var report = await NewSut(db, new PocFeedbackTriageResult
        {
            Items = { new PocFeedbackTriageVerdict { Index = 1, IsRequirementIssue = true, Reason = "r" } }
        }).ExecuteAsync(_projectId);

        Assert.Equal(2, report.Items.Count);
        Assert.True(report.Items[0].IsRequirementIssue);
        Assert.False(report.Items[1].IsRequirementIssue);
    }

    [Fact]
    public async Task NoBaAgent_StillListsComments_ButLocksTheRequirementPath()
    {
        AddComment("một ghi chú");

        await using var db = NewDb();
        var report = await NewSut(db, triaged: null).ExecuteAsync(_projectId);

        Assert.Equal(PocFeedbackTriageStatus.Ok, report.Status);
        Assert.False(report.BaConfigured);
        Assert.False(report.Classified);
        Assert.Single(report.Items);
    }

    [Fact]
    public async Task NoOpenComments_ReturnsNoOpenComments()
    {
        ConfigureBa();
        AddComment("đã gửi rồi", PocCommentStatus.Sent);

        await using var db = NewDb();
        var report = await NewSut(db, triaged: null).ExecuteAsync(_projectId);

        Assert.Equal(PocFeedbackTriageStatus.NoOpenComments, report.Status);
    }

    private void ConfigureBa()
    {
        using var db = NewDb();
        var model = new AiModel { Id = Guid.NewGuid(), ModelId = "m" };
        db.AiModels.Add(model);
        db.Agents.Add(new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = model.Id });
        db.SaveChanges();
    }

    private Guid AddComment(string text, PocCommentStatus status = PocCommentStatus.Open)
    {
        using var db = NewDb();
        var comment = new PocComment
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            PageView = "Danh sách",
            ElementLabel = "Nút Lưu",
            Comment = text,
            Status = status,
            CreatedAt = DateTime.UtcNow.AddSeconds(db.PocComments.Count())
        };
        db.PocComments.Add(comment);
        db.SaveChanges();
        return comment.Id;
    }

    private static TriagePocFeedbackUseCase NewSut(AppDbContext db, PocFeedbackTriageResult? triaged) =>
        new(db, new FakeLlm(triaged), new StubPrompts(), new BAAgentResolver(db));

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // triaged == null ⇒ giả lập lời gọi model hỏng.
    private sealed class FakeLlm : ILlmClient
    {
        private readonly PocFeedbackTriageResult? _triaged;

        public FakeLlm(PocFeedbackTriageResult? triaged) => _triaged = triaged;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
            => _triaged == null
                ? Task.FromResult((new LlmCallResult { IsSuccess = false }, (T?)null))
                : Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, _triaged as T));
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
    }
}
