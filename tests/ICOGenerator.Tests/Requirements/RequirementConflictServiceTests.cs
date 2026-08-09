using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Cổng soát MÂU THUẪN chạy ngay trước khi soạn Product Brief. Các test chốt đúng những điều khiến cổng
// này an toàn để bật mặc định: (1) tìm thấy thì lưu lại để lần bấm sau khỏi gọi LLM; (2) hội thoại KHÔNG
// đổi thì dùng lại kết quả cũ; (3) lời gọi lỗi thì FAIL-OPEN (không mâu thuẫn, không dời con trỏ);
// (4) chốt xong thì ghi ĐỦ CẶP lượt user+assistant (lượt user cụt sẽ làm màn hình chat kẹt) và gỡ cổng.
public class RequirementConflictServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };

    public RequirementConflictServiceTests()
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
    public async Task DetectAndStore_FindsConflicts_StoresThemAndMovesCursor()
    {
        var (project, ba) = await SeedAsync(turns: 4);
        var llm = new FakeLlm
        {
            Json = """
            { "conflicts": [ { "topic": "Số cấp duyệt", "sideA": "Quản lý duyệt là xong",
              "sideB": "HR duyệt thêm lần nữa", "question": "Đơn cần mấy cấp duyệt?",
              "options": ["Chỉ quản lý", "Quản lý rồi HR"] } ] }
            """
        };

        await using var db = NewDb();
        var tracked = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var conflicts = await NewSut(db, llm).DetectAndStoreAsync(tracked, trackedBa, _model);

        Assert.Single(conflicts);
        Assert.Equal("Đơn cần mấy cấp duyệt?", conflicts[0].Question);
        Assert.Equal(2, conflicts[0].Options.Count);
        Assert.Equal(4, tracked.ConflictCheckedTurnCount);
        Assert.NotNull(tracked.PendingConflicts);
    }

    [Fact]
    public async Task DetectAndStore_SameConversation_ReusesStoredResultWithoutCallingLlm()
    {
        var (project, ba) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Json = """{ "conflicts": [] }""" };

        await using var db = NewDb();
        var tracked = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).DetectAndStoreAsync(tracked, trackedBa, _model);
        await NewSut(db, llm).DetectAndStoreAsync(tracked, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task DetectAndStore_WhenLlmFails_FailsOpen_AndKeepsCursor()
    {
        var (project, ba) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Fail = true };

        await using var db = NewDb();
        var tracked = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var conflicts = await NewSut(db, llm).DetectAndStoreAsync(tracked, trackedBa, _model);

        Assert.Empty(conflicts);
        Assert.Equal(0, tracked.ConflictCheckedTurnCount);
        Assert.Null(tracked.PendingConflicts);
    }

    [Fact]
    public async Task DetectAndStore_IgnoresConflictsMissingBothSides()
    {
        var (project, ba) = await SeedAsync(turns: 2);
        var llm = new FakeLlm
        {
            Json = """
            { "conflicts": [ { "topic": "Thiếu vế", "sideA": "", "sideB": "gì đó", "question": "Hỏi gì?" },
                             { "topic": "Đủ", "sideA": "A", "sideB": "B", "question": "Chọn A hay B?", "options": ["A"] } ] }
            """
        };

        await using var db = NewDb();
        var tracked = await db.Projects.FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        var conflicts = await NewSut(db, llm).DetectAndStoreAsync(tracked, trackedBa, _model);

        Assert.Single(conflicts);
        // Một phương án là không bấm chọn được — service tự bù bằng chính hai vế đang chọi nhau.
        Assert.Equal(new[] { "A", "B" }, conflicts[0].Options);
    }

    [Fact]
    public async Task ApplyResolutions_WritesUserAndAssistantTurns_AndClearsGate()
    {
        var (project, ba) = await SeedAsync(turns: 2);

        await using var db = NewDb();
        var tracked = await db.Projects.FirstAsync(p => p.Id == project.Id);
        tracked.PendingConflicts = "[{}]";
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, new FakeLlm()).ApplyResolutionsAsync(
            tracked, trackedBa,
            new[] { new ConflictResolution("Đơn cần mấy cấp duyệt?", "Chỉ quản lý") },
            new BAConversationLog(db));

        var turns = await NewDb().AgentConversations
            .Where(c => c.ProjectId == project.Id)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToListAsync();

        Assert.Equal(4, turns.Count);
        Assert.Equal("user", turns[2].Role);
        Assert.Contains("Chỉ quản lý", turns[2].Message);
        // Lượt assistant đóng lượt là bắt buộc: hội thoại kết thúc bằng lượt user sẽ làm khung chat kẹt ở
        // "BA đang soạn câu trả lời…" (xem BAChatService.GetReplyStateAsync).
        Assert.Equal("assistant", turns[3].Role);
        Assert.Null(tracked.PendingConflicts);
        Assert.Equal(4, tracked.ConflictCheckedTurnCount);
    }

    [Fact]
    public void Parse_BadJson_ReturnsEmpty()
    {
        Assert.Empty(RequirementConflictService.Parse("{not json"));
        Assert.Empty(RequirementConflictService.Parse(null));
    }

    private RequirementConflictService NewSut(AppDbContext db, ILlmClient llm) =>
        new(db, llm, new StubPrompts(), NullLogger<RequirementConflictService>.Instance);

    private async Task<(Project Project, Agent Ba)> SeedAsync(int turns)
    {
        var ba = new Agent { Id = Guid.NewGuid(), Temperature = 0.2, AiModelId = _model.Id };
        var project = new Project { Id = Guid.NewGuid(), Name = "P", DecisionLog = "- Quản lý duyệt là xong" };

        await using var db = NewDb();
        db.Agents.Add(ba);
        db.Projects.Add(project);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < turns; i++)
        {
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = project.Id,
                AgentId = ba.Id,
                Role = i % 2 == 0 ? "user" : "assistant",
                Message = $"turn-{i}",
                CreatedAt = baseTime.AddSeconds(i)
            });
        }
        await db.SaveChangesAsync();
        return (project, ba);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Cổng soát đi qua đường structured output; đường ChatWithLogAsync không được dùng ở đây.
    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public string Json = """{ "conflicts": [] }""";
        public bool Fail;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            Calls++;
            var result = new LlmCallResult
            {
                IsSuccess = !Fail,
                Content = Fail ? string.Empty : Json,
                ErrorMessage = Fail ? "boom" : null
            };
            // Trả null cho phần structured để service đi qua đúng nhánh parse JSON thô — nhánh mà model
            // không bật structured output sẽ chạy trong thực tế.
            return Task.FromResult((result, (T?)null));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "soát mâu thuẫn\n\n{{input}}";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
