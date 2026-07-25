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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Sau khi F5 GIỮA lúc BA đang trả lời, lượt user đã được lưu nhưng lượt assistant còn đang sinh nền
// (ChatStream chạy với CancellationToken.None nên vẫn hoàn tất & lưu). IsReplyPendingAsync là nguồn để
// UI hiện lại khung "BA đang soạn…" rồi tự tải lại khi câu trả lời đã lưu — tránh bong bóng trả lời
// biến mất. Nó phải trả TRUE khi lượt hội thoại HIỆN HÀNH mới nhất là của user, và FALSE cho mọi trạng
// thái đã có (hoặc không cần) câu trả lời: lượt cuối là assistant, hội thoại trống, dự án lạ, và các lượt
// user cũ đã bị "New Chat" lưu trữ (ArchivedAt != null — bị global query filter loại).
public class BAChatReplyPendingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public BAChatReplyPendingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "app nghỉ phép" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Pending_WhenLatestTurnIsUser()
    {
        await SeedTurnsAsync(
            ("user", "Tôi muốn app quản lý đơn nghỉ phép"),
            ("assistant", "Đối tượng người dùng chính là ai?"),
            ("user", "Nhân viên toàn công ty")); // BA chưa kịp trả lời lượt này → đang chờ.

        await using var db = NewDb();
        Assert.True(await NewSut(db).IsReplyPendingAsync(_projectId));
    }

    [Fact]
    public async Task NotPending_WhenLatestTurnIsAssistant()
    {
        await SeedTurnsAsync(
            ("user", "Tôi muốn app quản lý đơn nghỉ phép"),
            ("assistant", "Đối tượng người dùng chính là ai?"));

        await using var db = NewDb();
        Assert.False(await NewSut(db).IsReplyPendingAsync(_projectId));
    }

    [Fact]
    public async Task NotPending_WhenNoConversationYet()
    {
        await using var db = NewDb();
        Assert.False(await NewSut(db).IsReplyPendingAsync(_projectId));
    }

    [Fact]
    public async Task NotPending_WhenProjectUnknown()
    {
        await using var db = NewDb();
        Assert.False(await NewSut(db).IsReplyPendingAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task NotPending_WhenTrailingUserTurnWasArchivedByNewChat()
    {
        // "New Chat" chỉ đóng dấu ArchivedAt (không xóa cứng). Một lượt user cũ còn "cụt" trong hội thoại
        // đã lưu trữ KHÔNG được coi là đang chờ — global query filter đã loại nó, hội thoại hiện hành trống.
        await SeedTurnsAsync(archived: true,
            ("user", "Ý tưởng cũ đã lưu trữ"));

        await using var db = NewDb();
        Assert.False(await NewSut(db).IsReplyPendingAsync(_projectId));
    }

    private Task SeedTurnsAsync(params (string Role, string Message)[] turns) => SeedTurnsAsync(false, turns);

    private async Task SeedTurnsAsync(bool archived, params (string Role, string Message)[] turns)
    {
        await using var db = NewDb();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < turns.Length; i++)
        {
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId,
                AgentId = _baId,
                Role = turns[i].Role,
                Message = turns[i].Message,
                CreatedAt = baseTime.AddSeconds(i),
                ArchivedAt = archived ? baseTime.AddMinutes(1) : null
            });
        }
        await db.SaveChangesAsync();
    }

    // Cùng harness dựng BAChatService như BAChatRetryTests (không scope factory ⇒ chạy tuần tự trên db test).
    private static BAChatService NewSut(AppDbContext db)
    {
        var config = new ConfigurationBuilder().Build();
        var prompts = new StubPrompts();
        var llm = new FakeLlm();
        return new BAChatService(
            db,
            llm,
            prompts,
            new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance),
            new BAChatReplyParser(),
            new ConversationMemoryService(db, llm, prompts),
            new UserMemoryService(db, llm, prompts),
            new RequirementCoverageService(db, llm, prompts),
            new OrganizationContextService(db, prompts, new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new DecisionLogService(db, llm, prompts),
            new InterviewOutlookService(db, llm, prompts),
            new ChecklistNoteStore(db));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeLlm : ILlmClient
    {
        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
            => throw new InvalidOperationException("IsReplyPendingAsync must not call the LLM.");
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
