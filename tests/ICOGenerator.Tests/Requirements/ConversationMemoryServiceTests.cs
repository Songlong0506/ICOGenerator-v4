using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bộ nhớ hội thoại: giữ cửa sổ lượt gần nhất nguyên văn (short-term) và GỘP DẦN các lượt cũ thành một
// tóm tắt bền (long-term). Các test chốt: (1) dưới ngưỡng thì KHÔNG tóm tắt — gửi nguyên văn tất cả;
// (2) đủ ngưỡng thì gộp lô lượt cũ, dời con trỏ, cửa sổ co lại; (3) tóm tắt lỗi thì fail-open (giữ
// nguyên, không mất lượt nào).
public class ConversationMemoryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Agent _ba;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };

    public ConversationMemoryServiceTests()
    {
        _ba = new Agent { Id = Guid.NewGuid(), Temperature = 0.2, AiModelId = _model.Id };

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        // BA agent + model: AgentConversation có FK (Restrict) tới Agent, nên phải tồn tại trước khi seed lượt.
        db.AiModels.Add(_model);
        db.Agents.Add(_ba);
        db.SaveChanges();
    }

    [Fact]
    public async Task LoadAsync_BelowThreshold_DoesNotSummarize_AndSendsAllVerbatim()
    {
        // 25 lượt: thừa hơn cửa sổ 5 lượt (< ngưỡng 10) ⇒ chưa gộp, gửi nguyên văn cả 25.
        var projectId = await SeedConversationAsync(turns: 25);
        var llm = new FakeLlm();

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        var sut = new ConversationMemoryService(db, llm, new StubPrompts());
        var memory = await sut.LoadAsync(project, _ba, _model);

        Assert.Null(memory.Summary);
        Assert.Equal(25, memory.RecentTurns.Count);
        Assert.Equal(0, llm.Calls);
        Assert.Equal(0, project.SummarizedTurnCount);
    }

    [Fact]
    public async Task LoadAsync_AtThreshold_FoldsOldestBatch_AndShrinksWindow()
    {
        // 30 lượt: thừa 10 (== ngưỡng) ⇒ gộp 10 lượt cũ nhất, còn 20 lượt gửi nguyên văn.
        var projectId = await SeedConversationAsync(turns: 30);
        var llm = new FakeLlm { Reply = "tóm tắt 10 lượt đầu" };

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        var sut = new ConversationMemoryService(db, llm, new StubPrompts());
        var memory = await sut.LoadAsync(project, _ba, _model);

        Assert.Equal(1, llm.Calls);
        Assert.Equal("tóm tắt 10 lượt đầu", memory.Summary);
        Assert.Equal(ConversationMemoryService.RecentWindowSize, memory.RecentTurns.Count);
        // Cửa sổ verbatim là PHẦN ĐUÔI: lượt đầu tiên còn lại là lượt thứ 11 (index 10, 0-based).
        Assert.Equal("turn-10", memory.RecentTurns[0].Message);

        // Con trỏ đã được lưu bền.
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == projectId);
        Assert.Equal(10, reloaded.SummarizedTurnCount);
        Assert.Equal("tóm tắt 10 lượt đầu", reloaded.ConversationSummary);
    }

    [Fact]
    public async Task LoadAsync_WhenSummaryCallFails_FailsOpen_KeepsAllTurnsAndNoPointerMove()
    {
        var projectId = await SeedConversationAsync(turns: 30);
        var llm = new FakeLlm { Fail = true };

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        var sut = new ConversationMemoryService(db, llm, new StubPrompts());
        var memory = await sut.LoadAsync(project, _ba, _model);

        Assert.Equal(1, llm.Calls);
        Assert.Null(memory.Summary);
        Assert.Equal(0, project.SummarizedTurnCount);
        // Không gộp được ⇒ KHÔNG mất lượt nào: cả 30 vẫn gửi nguyên văn.
        Assert.Equal(30, memory.RecentTurns.Count);
    }

    [Fact]
    public async Task LoadAsync_FoldsIncrementally_AcrossTwoBatches()
    {
        var projectId = await SeedConversationAsync(turns: 30);
        var llm = new FakeLlm { Reply = "S1" };

        // Lô 1: 30 lượt ⇒ gộp 10, con trỏ = 10.
        await using (var db = NewDb())
        {
            var project = await db.Projects.FirstAsync(p => p.Id == projectId);
            await new ConversationMemoryService(db, llm, new StubPrompts()).LoadAsync(project, _ba, _model);
        }

        // Thêm 10 lượt nữa (tổng 40): thừa = 40-10-20 = 10 == ngưỡng ⇒ gộp tiếp 10, con trỏ = 20.
        await AppendTurnsAsync(projectId, from: 30, count: 10);
        llm.Reply = "S2";
        await using (var db = NewDb())
        {
            var project = await db.Projects.FirstAsync(p => p.Id == projectId);
            var memory = await new ConversationMemoryService(db, llm, new StubPrompts()).LoadAsync(project, _ba, _model);
            Assert.Equal("S2", memory.Summary);
            Assert.Equal(20, memory.RecentTurns.Count);
            Assert.Equal("turn-20", memory.RecentTurns[0].Message);
        }

        Assert.Equal(2, llm.Calls);
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == projectId);
        Assert.Equal(20, reloaded.SummarizedTurnCount);
    }

    private async Task<Guid> SeedConversationAsync(int turns)
    {
        var projectId = Guid.NewGuid();
        await using var db = NewDb();
        db.Projects.Add(new Project { Id = projectId, Name = "P" });
        await db.SaveChangesAsync();
        await AppendTurnsAsync(projectId, from: 0, count: turns);
        return projectId;
    }

    private async Task AppendTurnsAsync(Guid projectId, int from, int count)
    {
        await using var db = NewDb();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = from; i < from + count; i++)
        {
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = projectId,
                AgentId = _ba.Id,
                Role = i % 2 == 0 ? "user" : "assistant",
                Message = $"turn-{i}",
                CreatedAt = baseTime.AddSeconds(i)
            });
        }
        await db.SaveChangesAsync();
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Fake ILlmClient: chỉ phục vụ đường tóm tắt (ChatWithLogAsync). Đếm số lần gọi và trả/đẩy lỗi theo cấu hình.
    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public string Reply = "summary";
        public bool Fail;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new LlmCallResult
            {
                IsSuccess = !Fail,
                Content = Fail ? string.Empty : Reply,
                ErrorMessage = Fail ? "boom" : null
            });
        }

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
            => throw new NotSupportedException();
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## tóm tắt";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
