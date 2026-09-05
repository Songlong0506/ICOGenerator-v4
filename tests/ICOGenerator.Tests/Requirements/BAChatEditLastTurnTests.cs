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

// "✎ sửa" lượt vừa gửi. Các test chốt: (1) ghi ĐÈ lượt user cuối + xóa câu trả lời cũ, không nhân đôi
// lượt; (2) KÉO LÙI mọi con trỏ gộp — nếu không, con trỏ vượt số lượt hiện có và bản đồ bao phủ/nhật ký
// đóng băng vĩnh viễn ở bản dựng từ câu đã bị sửa; (3) sửa được cả lượt user "cụt" (câu trả lời đã chết).
public class BAChatEditLastTurnTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public BAChatEditLastTurnTests()
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
    public async Task Edit_OverwritesLastUserTurn_DeletesOldReply_AndAnswersAgain()
    {
        await SeedTurnsAsync(
            ("user", "Tôi muốn app quản lý đơn nghỉ phép"),
            ("assistant", "Đối tượng người dùng chính là ai?"),
            ("user", "Chỉ có 5 nhân viên"),
            ("assistant", "Quy trình duyệt hiện tại thế nào?"));
        var llm = new FakeLlm { ChatReply = new BAChatReply { Message = "50 người thì có duyệt theo phòng ban không?" } };

        await using var db = NewDb();
        var result = await NewSut(db, llm).EditLastUserTurnAsync(_projectId, "Khoảng 50 nhân viên");

        Assert.Equal(ChatWithBAResult.Ok, result.Status);

        await using var verify = NewDb();
        var turns = await verify.AgentConversations
            .Where(c => c.ProjectId == _projectId)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToListAsync();

        Assert.Equal(4, turns.Count);
        Assert.Equal(2, turns.Count(t => t.Role == "user"));
        Assert.Equal("Khoảng 50 nhân viên", turns[2].Message);
        Assert.DoesNotContain(turns, t => t.Message == "Chỉ có 5 nhân viên");
        Assert.Equal("50 người thì có duyệt theo phòng ban không?", turns[3].Message);
    }

    [Fact]
    public async Task Edit_ClampsHarvestCursors_SoDistilledMapsRebuildFromNewText()
    {
        await SeedTurnsAsync(
            ("user", "Tôi muốn app quản lý đơn nghỉ phép"),
            ("assistant", "Bao nhiêu người dùng?"),
            ("user", "Chỉ có 5 nhân viên"),
            ("assistant", "Quy trình duyệt hiện tại thế nào?"));

        await using (var seed = NewDb())
        {
            var p = await seed.Projects.FirstAsync(x => x.Id == _projectId);
            p.CoverageHarvestedTurnCount = 4;
            p.UserMemoryHarvestedTurnCount = 4;
            p.SummarizedTurnCount = 4;
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb();
        await NewSut(db, new FakeLlm()).EditLastUserTurnAsync(_projectId, "Khoảng 50 nhân viên");

        var project = await NewDb().Projects.FirstAsync(x => x.Id == _projectId);
        // Sau khi sửa còn 4 lượt (3 cũ + 1 trả lời mới), nhưng con trỏ phải đứng TRƯỚC lượt vừa sửa (=2)
        // để lượt gộp kế tiếp đọc lại chính câu đã sửa.
        Assert.Equal(2, project.CoverageHarvestedTurnCount);
        Assert.Equal(2, project.UserMemoryHarvestedTurnCount);
        Assert.Equal(2, project.SummarizedTurnCount);
    }

    [Fact]
    public async Task Edit_OrphanedUserTurn_RewritesItWithoutDeletingAnything()
    {
        await SeedTurnsAsync(
            ("user", "Tôi muốn app quản lý đơn nghỉ phép"),
            ("assistant", "Bao nhiêu người dùng?"),
            ("user", "Chỉ có 5 nhân viên"));
        var llm = new FakeLlm { ChatReply = new BAChatReply { Message = "Ai duyệt đơn?" } };

        await using var db = NewDb();
        var result = await NewSut(db, llm).EditLastUserTurnAsync(_projectId, "Khoảng 50 nhân viên");

        Assert.Equal(ChatWithBAResult.Ok, result.Status);

        var turns = await NewDb().AgentConversations
            .Where(c => c.ProjectId == _projectId)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToListAsync();
        Assert.Equal(4, turns.Count);
        Assert.Equal("Khoảng 50 nhân viên", turns[2].Message);
        Assert.Equal("Ai duyệt đơn?", turns[3].Message);
    }

    [Fact]
    public async Task Edit_EmptyMessageOrEmptyConversation_IsRefused()
    {
        await using var db = NewDb();
        var llm = new FakeLlm();

        Assert.Equal(ChatWithBAResult.NothingToRetry, (await NewSut(db, llm).EditLastUserTurnAsync(_projectId, "   ")).Status);
        Assert.Equal(ChatWithBAResult.NothingToRetry, (await NewSut(db, llm).EditLastUserTurnAsync(_projectId, "gì đó")).Status);
        Assert.Equal(0, llm.ChatCalls);
    }

    [Fact]
    public async Task Edit_ProjectNotFound_ReturnsProjectNotFound()
    {
        await using var db = NewDb();
        var result = await NewSut(db, new FakeLlm()).EditLastUserTurnAsync(Guid.NewGuid(), "gì đó");
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

        // Các bước gộp (bản đồ bao phủ, bộ nhớ…) đi đường này — trả thất bại để chúng fail-open, giữ test
        // tập trung vào chính lượt chat.
        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {

            // Bản đồ bao phủ nay đi qua đường structured output (RequirementCoverageService). Fake trả về
            // Value null để service rơi xuống nhánh parse văn xuôi — đúng nhánh mà một model không nhận
            // response_format sẽ chạy — nên các test ở đây vẫn seed bản đồ bằng text như trước.
            if (logContext.Purpose == "BARequirementCoverage")
                return Task.FromResult((ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).Result, (T?)null));
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
