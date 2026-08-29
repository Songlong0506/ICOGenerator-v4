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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ICOGenerator.Tests;

namespace ICOGenerator.Tests.Requirements;

// Đóng vòng học từ ghi chú POC → checklist học được của BA. Các test chốt: (1) không có ghi chú Sent
// mới thì không gọi LLM; (2) harvest bình thường ghi bài học KÈM LÝ DO + nguồn, dời con trỏ; (3) lỗi LLM
// thì fail-open (giữ bài học cũ, con trỏ đứng yên); (4) vòng sau chỉ gộp ghi chú MỚI kể từ con trỏ.
public class PocFeedbackMemoryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };

    private const string OneLesson = """
        {"items":[{"text":"Hỏi đủ các cột của bảng tính tiền.","rationale":"POC thiếu cột phụ cấp vì phỏng vấn không hỏi hết các khoản thành phần của bảng tính.","evidence":"bảng lương thiếu cột phụ cấp"}]}
        """;

    public PocFeedbackMemoryServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.SaveChanges();
    }

    [Fact]
    public async Task TryHarvestAsync_NoSentComments_DoesNotCallLlm()
    {
        var project = await SeedAsync(sentComments: 0, openComments: 2);
        var llm = new FakeLlm();

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task TryHarvestAsync_WithSentComments_StoresLessonWithReason_AndAdvancesCursor()
    {
        var project = await SeedAsync(sentComments: 3, openComments: 1);
        var llm = new FakeLlm { Reply = OneLesson };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(1, llm.Calls);
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Equal(3, reloaded.PocFeedbackHarvestedCount);

        var item = await NewDb().AgentChecklistItems.SingleAsync();
        Assert.Equal("Hỏi đủ các cột của bảng tính tiền.", item.Text);
        Assert.StartsWith("POC thiếu cột phụ cấp", item.Rationale);
        Assert.Equal("bảng lương thiếu cột phụ cấp", item.Evidence);
        Assert.Equal(ChecklistItemSource.PocFeedback, item.SourceKind);
        Assert.Equal(project.Id, item.SourceProjectId);
    }

    [Fact]
    public async Task TryHarvestAsync_LlmFails_FailsOpen()
    {
        var project = await SeedAsync(sentComments: 2, openComments: 0, existingLesson: "Bài học cũ.");
        var llm = new FakeLlm { Fail = true };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(1, llm.Calls);
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Equal(0, reloaded.PocFeedbackHarvestedCount);
        Assert.Equal("Bài học cũ.", (await NewDb().AgentChecklistItems.SingleAsync()).Text);
    }

    [Fact]
    public async Task TryHarvestAsync_SecondRound_OnlyDistillsNewComments()
    {
        var project = await SeedAsync(sentComments: 2, openComments: 0);
        var llm = new FakeLlm { Reply = OneLesson };

        await using (var db = NewDb())
        {
            await NewSut(db, llm).TryHarvestAsync(project.Id);
        }

        // Thêm 1 ghi chú Sent mới rồi harvest vòng hai: chỉ ghi chú mới được đưa vào prompt.
        await using (var db = NewDb())
        {
            db.PocComments.Add(NewComment(project.Id, "ghi chú mới nhất", PocCommentStatus.Sent, offsetSeconds: 100));
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            await NewSut(db, llm).TryHarvestAsync(project.Id);
        }

        Assert.Equal(2, llm.Calls);
        Assert.Contains("ghi chú mới nhất", llm.LastUserMessage);
        Assert.DoesNotContain("ghi chú cũ 0", llm.LastUserMessage);
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Equal(3, reloaded.PocFeedbackHarvestedCount);
    }

    private PocFeedbackMemoryService NewSut(AppDbContext db, ILlmClient llm) =>
        new(db, llm, new StubPrompts(), new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)), NullLogger<PocFeedbackMemoryService>.Instance);

    private async Task<Project> SeedAsync(int sentComments, int openComments, string? existingLesson = null)
    {
        var ba = new Agent
        {
            Id = Guid.NewGuid(),
            RoleKey = AgentRoleKey.BusinessAnalyst,
            Temperature = 0.2,
            AiModelId = _model.Id
        };
        var project = new Project { Id = Guid.NewGuid(), Name = "P" };

        await using var db = NewDb();
        db.Agents.Add(ba);
        db.Projects.Add(project);
        if (existingLesson != null)
            db.AgentChecklistItems.Add(new AgentChecklistItem { AgentId = ba.Id, Text = existingLesson });
        for (var i = 0; i < sentComments; i++)
            db.PocComments.Add(NewComment(project.Id, $"ghi chú cũ {i}", PocCommentStatus.Sent, i));
        for (var i = 0; i < openComments; i++)
            db.PocComments.Add(NewComment(project.Id, $"ghi chú open {i}", PocCommentStatus.Open, 50 + i));
        await db.SaveChangesAsync();
        return project;
    }

    private static PocComment NewComment(Guid projectId, string comment, PocCommentStatus status, int offsetSeconds) => new()
    {
        ProjectId = projectId,
        PageView = "Danh sách",
        ElementLabel = "Bảng",
        Comment = comment,
        Status = status,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(offsetSeconds)
    };

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public string Reply = OneLesson;
        public bool Fail;
        public string LastUserMessage = string.Empty;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastUserMessage = messages.Last().Text ?? string.Empty;
            return Task.FromResult(new LlmCallResult
            {
                IsSuccess = !Fail,
                Content = Fail ? string.Empty : Reply,
                ErrorMessage = Fail ? "boom" : null
            });
        }

        public async Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
            => (await ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken), null);
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## rút kinh nghiệm từ ghi chú POC";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
