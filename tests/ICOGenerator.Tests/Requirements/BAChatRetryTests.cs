using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Organization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ICOGenerator.Tests;

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
    public void TryBeginExclusive_RefusesWhileAnotherTurnIsRunning_AndFreesUpAfterwards()
    {
        // Điểm vào "Thử lại" (ChatStream) giành chỗ độc quyền qua sổ theo dõi: lượt đang được trả lời ở
        // tab/request khác thì retry phải nhường, không thì hai câu trả lời cho cùng một câu hỏi. Kiểm
        // tra + ghi dấu là MỘT thao tác nguyên tử nên hai request cùng lúc không thể cùng thắng.
        var tracker = new BAChatTurnTracker();

        var firstWon = tracker.TryBeginExclusive(_projectId, out var first);
        var secondWon = tracker.TryBeginExclusive(_projectId, out var second);

        Assert.True(firstWon);
        Assert.False(secondWon);
        Assert.Null(second);
        Assert.True(tracker.IsRunning(_projectId));

        first!.Dispose();
        Assert.False(tracker.IsRunning(_projectId));
        Assert.True(tracker.TryBeginExclusive(_projectId, out var third));
        third!.Dispose();
    }

    [Fact]
    public void Begin_IsRefCounted_SoOverlappingTurnsDoNotClearEachOthersMark()
    {
        var tracker = new BAChatTurnTracker();
        var outer = tracker.Begin(_projectId);
        var inner = tracker.Begin(_projectId);

        inner.Dispose();
        Assert.True(tracker.IsRunning(_projectId)); // lượt ngoài vẫn đang chạy

        outer.Dispose();
        Assert.False(tracker.IsRunning(_projectId));
    }

    [Fact]
    public async Task Retry_OrphanedUserTurn_RerunsThatTurn_WithoutAddingUserTurn()
    {
        // Lượt user "cụt" mà KHÔNG còn ai đang trả lời: câu trả lời cũ đã chết (server khởi động lại giữa
        // chừng, mạng vỡ). Đây là trạng thái làm treo màn hình ở "BA đang soạn câu trả lời…" — nút "Thử
        // lại" phải chạy lại đúng lượt đó, không bắt người dùng gõ lại câu hỏi và không nhân đôi lượt user.
        await SeedTurnsAsync(
            ("user", "Tôi muốn app quản lý đơn nghỉ phép"),
            ("assistant", "Đối tượng người dùng chính là ai?"),
            ("user", "Nhân viên toàn công ty"));
        var llm = new FakeLlm { ChatReply = new BAChatReply { Message = "Quy trình duyệt hiện tại thế nào?" } };

        await using var db = NewDb();
        var result = await NewSut(db, llm, new BAChatTurnTracker()).RetryLastTurnAsync(_projectId);

        Assert.Equal(ChatWithBAResult.Ok, result.Status);
        Assert.Equal("Quy trình duyệt hiện tại thế nào?", result.Reply);

        await using var verify = NewDb();
        var turns = await verify.AgentConversations
            .Where(c => c.ProjectId == _projectId)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToListAsync();
        Assert.Equal(4, turns.Count);
        Assert.Equal(2, turns.Count(t => t.Role == "user"));
        Assert.Equal("assistant", turns[3].Role);
        Assert.Equal("Quy trình duyệt hiện tại thế nào?", turns[3].Message);
    }

    [Fact]
    public async Task Chat_WhenTurnBlowsUp_ClosesConversationWithFailureTurn()
    {
        // Lượt user được lưu TRƯỚC khi gọi LLM. Nếu lượt vỡ bằng ngoại lệ (mạng đứt, hạ tầng lỗi) mà
        // không đóng lượt, hội thoại nằm lại ở "lượt cuối là user" ⇒ trang Requirements treo vĩnh viễn ở
        // "BA đang soạn câu trả lời…", F5 cũng không thoát và không gửi được tin mới.
        var llm = new FakeLlm { ThrowOnChat = new HttpRequestException("connection reset") };

        await using var db = NewDb();
        await Assert.ThrowsAsync<HttpRequestException>(
            () => NewSut(db, llm).ChatAsync(_projectId, "Tôi muốn app quản lý đơn nghỉ phép"));

        await using var verify = NewDb();
        var turns = await verify.AgentConversations
            .Where(c => c.ProjectId == _projectId)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToListAsync();
        Assert.Equal(2, turns.Count);
        Assert.Equal("user", turns[0].Role);
        Assert.Equal("assistant", turns[1].Role); // hội thoại KHÔNG kết thúc bằng lượt user
        Assert.StartsWith(ConversationTranscriptBuilder.LlmFailurePrefix, turns[1].Message);
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
    private static BAChatService NewSut(AppDbContext db, ILlmClient llm, BAChatTurnTracker? tracker = null)
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
            new RequirementCoverageService(db, llm, prompts, new CoverageChecklist(prompts)),
            new OrganizationContextService(db, prompts,
                new OrgChartProvider(db, new MemoryCache(new MemoryCacheOptions())),
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new InterviewOutlookService(db, llm, prompts),
            new InterviewScopeService(db, llm, prompts),
            new ScreenStepPlacementService(llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: tracker);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeLlm : ILlmClient
    {
        public BAChatReply ChatReply = new() { Message = "Đã ghi nhận." };
        public int ChatCalls;
        // Ngoại lệ NÉM RA từ lời gọi LLM (khác với IsSuccess=false): mô phỏng mạng đứt / hạ tầng lỗi.
        public Exception? ThrowOnChat;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {

            // Bản đồ bao phủ nay đi qua đường structured output (RequirementCoverageService). Fake trả về
            // Value null để service rơi xuống nhánh parse văn xuôi — đúng nhánh mà một model không nhận
            // response_format sẽ chạy — nên các test ở đây vẫn seed bản đồ bằng text như trước.
            if (logContext.Purpose == "BARequirementCoverage")
                return Task.FromResult((ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).Result, (T?)null));
            if (ThrowOnChat != null && logContext.Purpose == "BAChat")
                throw ThrowOnChat;

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
    }
}
