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

// Nút "Thử lại" cho lượt BA bị lỗi LLM: RetryLastTurnAsync phải (1) xóa ĐÚNG lượt lỗi cuối rồi chạy lại
// lượt chat mà KHÔNG ghi thêm lượt user nào — người dùng khỏi gõ lại câu hỏi; (2) từ chối
// (NothingToRetry) khi lượt cuối không phải thông báo lỗi (user đã nhắn thêm / tab khác retry trước) để
// không chạy đúp một lượt đã có trả lời.
public class BAChatRetryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    private static readonly string FailureMessage =
        ConversationTranscriptBuilder.LlmFailurePrefix + ", chưa thể trả lời. Chi tiết: timeout";

    public BAChatRetryTests()
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
    public async Task Retry_LastTurnIsFailure_DeletesIt_RerunsTurn_WithoutAddingUserTurn()
    {
        await SeedTurnsAsync(("user", "Tôi muốn app quản lý đơn nghỉ phép"), ("assistant", FailureMessage));
        var llm = new FakeLlm { ChatReply = new BAChatReply { Message = "Đối tượng người dùng chính là ai?" } };

        await using var db = NewDb();
        var result = await NewSut(db, llm).RetryLastTurnAsync(_projectId);

        Assert.Equal(ChatWithBAResult.Ok, result.Status);
        Assert.Equal("Đối tượng người dùng chính là ai?", result.Reply);

        await using var verify = NewDb();
        var turns = await verify.AgentConversations
            .Where(c => c.ProjectId == _projectId)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToListAsync();
        // Vẫn đúng MỘT lượt user (không ghi thêm) + MỘT lượt assistant mới thay cho lượt lỗi đã xóa.
        Assert.Equal(2, turns.Count);
        Assert.Equal("user", turns[0].Role);
        Assert.Equal("assistant", turns[1].Role);
        Assert.Equal("Đối tượng người dùng chính là ai?", turns[1].Message);
        Assert.DoesNotContain(turns, t => (t.Message ?? "").StartsWith(ConversationTranscriptBuilder.LlmFailurePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retry_LastTurnIsNormalAssistantReply_ReturnsNothingToRetry_AndDeletesNothing()
    {
        await SeedTurnsAsync(("user", "Tôi muốn app quản lý đơn nghỉ phép"), ("assistant", "Đối tượng người dùng chính là ai?"));
        var llm = new FakeLlm();

        await using var db = NewDb();
        var result = await NewSut(db, llm).RetryLastTurnAsync(_projectId);

        Assert.Equal(ChatWithBAResult.NothingToRetry, result.Status);
        await using var verify = NewDb();
        Assert.Equal(2, await verify.AgentConversations.CountAsync(c => c.ProjectId == _projectId));
        Assert.Equal(0, llm.ChatCalls);
    }

    [Fact]
    public async Task Retry_UserAlreadySentANewMessageAfterTheFailure_ReturnsNothingToRetry()
    {
        // Lượt lỗi KHÔNG còn là lượt cuối (user đã nhắn tiếp) — retry lúc này sẽ chạy đúp lượt đang xử
        // lý ở tab kia / lượt sắp được trả lời, nên phải bị từ chối.
        await SeedTurnsAsync(
            ("user", "Tôi muốn app quản lý đơn nghỉ phép"),
            ("assistant", FailureMessage),
            ("user", "Bạn còn đó không?"));
        var llm = new FakeLlm();

        await using var db = NewDb();
        var result = await NewSut(db, llm).RetryLastTurnAsync(_projectId);

        Assert.Equal(ChatWithBAResult.NothingToRetry, result.Status);
        await using var verify = NewDb();
        Assert.Equal(3, await verify.AgentConversations.CountAsync(c => c.ProjectId == _projectId));
    }

    [Fact]
    public async Task Retry_ProjectNotFound_ReturnsProjectNotFound()
    {
        await using var db = NewDb();
        var result = await NewSut(db, new FakeLlm()).RetryLastTurnAsync(Guid.NewGuid());
        Assert.Equal(ChatWithBAResult.ProjectNotFound, result.Status);
    }

    private async Task SeedTurnsAsync(params (string Role, string Message)[] turns)
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
                CreatedAt = baseTime.AddSeconds(i)
            });
        }
        await db.SaveChangesAsync();
    }

    // Cùng harness dựng BAChatService như RequirementReadinessGateTests (không scope factory ⇒ các bước
    // chuẩn bị chạy tuần tự trên chính db của test).
    private static BAChatService NewSut(AppDbContext db, ILlmClient llm)
    {
        var config = new ConfigurationBuilder().Build();
        var prompts = new StubPrompts();
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
        public BAChatReply ChatReply = new() { Message = "Đã ghi nhận." };
        public int ChatCalls;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            object? value = logContext.Purpose switch
            {
                "BAChat" => ChatReply,
                _ => throw new InvalidOperationException($"Unexpected structured call: {logContext.Purpose}")
            };
            ChatCalls++;
            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)value));
        }
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
