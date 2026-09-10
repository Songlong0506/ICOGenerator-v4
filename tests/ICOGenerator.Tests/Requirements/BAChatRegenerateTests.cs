using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
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

// Nút "↻ thử lại" trên bong bóng NGƯỜI DÙNG (RegenerateLastReplyAsync): BA soạn lại câu trả lời cho
// chính câu hỏi đó. Bốn điều phải giữ, vì mất bất kỳ điều nào thì nút này biến thành một đường làm bẩn
// hội thoại thay vì một đường sửa nó:
//   (1) câu hỏi KHÔNG đổi và KHÔNG có lượt user nào được ghi thêm — nếu không, mỗi lần bấm là một lượt
//       user giả đi thẳng vào bản đồ bao phủ và bộ nhớ hội thoại;
//   (2) câu trả lời cũ bị XÓA, không để hai câu trả lời cho một câu hỏi nằm cạnh nhau;
//   (3) các con trỏ gộp được kéo lùi — xóa một lượt làm số lượt giảm đi, con trỏ vượt quá số lượt hiện
//       có thì mọi lượt gộp sau này thấy delta rỗng và bản đồ/nhật ký đóng băng vĩnh viễn;
//   (4) không có gì để soạn lại (hội thoại rỗng / lượt assistant không có lượt user đứng trước) ⇒
//       NothingToRetry, để UI mời tải lại trang thay vì chạy một lượt vô nghĩa.
public class BAChatRegenerateTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public BAChatRegenerateTests()
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
    public async Task Regenerate_ReplacesLastReply_KeepsQuestion_AndAddsNoUserTurn()
    {
        await SeedTurnsAsync(
            ("user", "Tôi muốn app quản lý đơn nghỉ phép"),
            ("assistant", "Đối tượng người dùng chính là ai?"));
        var llm = new FakeLlm { ChatReply = new BAChatReply { Message = "Ai là người duyệt đơn ạ?" } };

        await using var db = NewDb();
        var result = await NewSut(db, llm).RegenerateLastReplyAsync(_projectId);

        Assert.Equal(ChatWithBAResult.Ok, result.Status);
        Assert.Equal("Ai là người duyệt đơn ạ?", result.Reply);

        await using var verify = NewDb();
        var turns = await ReadTurnsAsync(verify);
        // Vẫn đúng hai lượt: câu hỏi cũ nguyên văn + MỘT câu trả lời mới thay chỗ câu trả lời cũ.
        Assert.Equal(2, turns.Count);
        Assert.Equal(1, turns.Count(t => t.Role == "user"));
        Assert.Equal("Tôi muốn app quản lý đơn nghỉ phép", turns[0].Message);
        Assert.Equal("assistant", turns[1].Role);
        Assert.Equal("Ai là người duyệt đơn ạ?", turns[1].Message);
        Assert.DoesNotContain(turns, t => t.Message == "Đối tượng người dùng chính là ai?");
    }

    [Fact]
    public async Task Regenerate_PullsMergeCursorsBack_SoDistillationsDoNotFreeze()
    {
        // Con trỏ đang trỏ tới lượt thứ 5 của một hội thoại 5 lượt. Soạn lại xóa một lượt ⇒ nếu con trỏ
        // đứng yên, nó vượt quá số lượt hiện có và mọi lượt gộp sau này thấy delta rỗng: bản đồ bao phủ,
        // bảng màn hình, hồ sơ user và bản tóm tắt hội thoại đóng băng vĩnh viễn.
        await SeedTurnsAsync(
            ("user", "Tôi muốn app quản lý đơn nghỉ phép"),
            ("assistant", "Đối tượng người dùng chính là ai?"),
            ("user", "Nhân viên toàn công ty"),
            ("assistant", "Quy trình duyệt hiện tại thế nào?"),
            ("user", "Trưởng bộ phận duyệt là xong"));
        await using (var seed = NewDb())
        {
            var project = await seed.Projects.FirstAsync(x => x.Id == _projectId);
            project.CoverageHarvestedTurnCount = 5;
            project.InterviewScopeHarvestedTurnCount = 5;
            project.UserMemoryHarvestedTurnCount = 5;
            project.SummarizedTurnCount = 5;
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        var result = await NewSut(db, new FakeLlm { ChatReply = new BAChatReply { Message = "Có cần cấp thay mặt duyệt không ạ?" } })
            .RegenerateLastReplyAsync(_projectId);
        Assert.Equal(ChatWithBAResult.Ok, result.Status);

        await using var verify = NewDb();
        var turnCount = await verify.AgentConversations.CountAsync(c => c.ProjectId == _projectId);
        var after = await verify.Projects.AsNoTracking().FirstAsync(x => x.Id == _projectId);
        // Lượt cuối là USER (câu trả lời chưa tới) nên không có gì bị xóa — nhưng con trỏ vẫn phải nằm
        // TRƯỚC lượt user đang được trả lời, tức không bao giờ vượt quá số lượt hiện có.
        Assert.True(after.CoverageHarvestedTurnCount <= turnCount);
        Assert.True(after.InterviewScopeHarvestedTurnCount <= turnCount);
        Assert.True(after.UserMemoryHarvestedTurnCount <= turnCount);
        Assert.True(after.SummarizedTurnCount <= turnCount);
        Assert.True(after.SummarizedTurnCount < 5);
    }

    [Fact]
    public async Task Regenerate_OrphanedUserTurn_RunsThatTurn_WithoutDeletingAnything()
    {
        // Lượt user còn "cụt" (câu trả lời đã chết): nút nằm sẵn trên chính bong bóng đó, và bấm nó phải
        // chạy đúng lượt ấy — không xóa gì, không bắt người dùng gõ lại câu hỏi.
        await SeedTurnsAsync(
            ("user", "Tôi muốn app quản lý đơn nghỉ phép"),
            ("assistant", "Đối tượng người dùng chính là ai?"),
            ("user", "Nhân viên toàn công ty"));
        var llm = new FakeLlm { ChatReply = new BAChatReply { Message = "Quy trình duyệt hiện tại thế nào?" } };

        await using var db = NewDb();
        var result = await NewSut(db, llm).RegenerateLastReplyAsync(_projectId);

        Assert.Equal(ChatWithBAResult.Ok, result.Status);

        await using var verify = NewDb();
        var turns = await ReadTurnsAsync(verify);
        Assert.Equal(4, turns.Count);
        Assert.Equal(2, turns.Count(t => t.Role == "user"));
        Assert.Equal("Nhân viên toàn công ty", turns[2].Message);
        Assert.Equal("Quy trình duyệt hiện tại thế nào?", turns[3].Message);
    }

    [Fact]
    public async Task Regenerate_LastReplyHasNoUserTurnBeforeIt_ReturnsNothingToRetry()
    {
        // Lượt assistant đứng một mình (lượt chào, hoặc lượt do một đường khác ghi vào): không có câu hỏi
        // nào để trả lời lại, nên đừng xóa lượt đó đi rồi chạy một lượt trống.
        await SeedTurnsAsync(("assistant", "Chào anh/chị, mình là BA."));
        var llm = new FakeLlm();

        await using var db = NewDb();
        var result = await NewSut(db, llm).RegenerateLastReplyAsync(_projectId);

        Assert.Equal(ChatWithBAResult.NothingToRetry, result.Status);
        Assert.Equal(0, llm.ChatCalls);
        await using var verify = NewDb();
        Assert.Equal(1, await verify.AgentConversations.CountAsync(c => c.ProjectId == _projectId));
    }

    [Fact]
    public async Task Regenerate_EmptyConversation_ReturnsNothingToRetry()
    {
        var llm = new FakeLlm();
        await using var db = NewDb();
        var result = await NewSut(db, llm).RegenerateLastReplyAsync(_projectId);

        Assert.Equal(ChatWithBAResult.NothingToRetry, result.Status);
        Assert.Equal(0, llm.ChatCalls);
    }

    [Fact]
    public async Task Regenerate_ProjectNotFound_ReturnsProjectNotFound()
    {
        await using var db = NewDb();
        var result = await NewSut(db, new FakeLlm()).RegenerateLastReplyAsync(Guid.NewGuid());
        Assert.Equal(ChatWithBAResult.ProjectNotFound, result.Status);
    }

    private Task<List<AgentConversation>> ReadTurnsAsync(AppDbContext db) => db.AgentConversations
        .Where(c => c.ProjectId == _projectId)
        .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
        .ToListAsync();

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

    // Cùng harness dựng BAChatService như BAChatRetryTests (không scope factory ⇒ các bước chuẩn bị chạy
    // tuần tự trên chính db của test).
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
            new RequirementCoverageService(db, llm, prompts, new CoverageChecklist(prompts)),
            new OrganizationContextService(db, prompts,
                new OrgChartProvider(db, new MemoryCache(new MemoryCacheOptions())),
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new InterviewScopeService(db, llm, prompts),
            new ScreenStepPlacementService(llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
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
            // Như BAChatRetryTests: các lời gọi phụ trợ (bản đồ bao phủ…) rơi xuống nhánh fail-open, chỉ
            // lượt chat thật trả về nội dung.
            if (logContext.Purpose != "BAChat")
                return Task.FromResult((ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).Result, (T?)null));

            ChatCalls++;
            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)(object)ChatReply));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }
}
