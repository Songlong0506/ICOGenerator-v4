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
// nguyên, không mất lượt nào); (4) NGƯỠNG ĐO BẰNG TOKEN chứ không đếm lượt — vài lượt rất dài cũng đủ
// kích hoạt, còn nhiều lượt ngắn thì không.
//
// Model của các test: ContextWindow mặc định 128.000 ⇒ PromptBudget.ConversationTokens = 20.000, tức
// RecentWindowTokensFor = 20.000. Mỗi lượt seed dài 1.000 ký tự ⇒ ~252 token sau khi render, nên 40 lượt
// (~10.080 token) vẫn nằm dưới trần token và TRẦN LƯỢT (40) là cái chặn — trừ các test cố ý dựng lượt dài.
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
        // 45 lượt: thừa 5 lượt ngoài cửa sổ 40, nhưng chỉ ~1.260 token (< ngưỡng 5.000) ⇒ chưa gộp.
        var projectId = await SeedConversationAsync(turns: 45);
        var llm = new FakeLlm();

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        var sut = new ConversationMemoryService(db, llm, new StubPrompts());
        var memory = await sut.LoadAsync(project, _ba, _model);

        Assert.Null(memory.Summary);
        Assert.Equal(45, memory.RecentTurns.Count);
        Assert.Equal(0, llm.Calls);
        Assert.Equal(0, project.SummarizedTurnCount);
    }

    [Fact]
    public async Task LoadAsync_AtThreshold_FoldsOldestBatch_AndShrinksWindow()
    {
        // 60 lượt: thừa 20 lượt ngoài cửa sổ 40 ⇒ ~5.040 token, vừa đạt ngưỡng 5.000 ⇒ gộp 20 lượt cũ nhất.
        var projectId = await SeedConversationAsync(turns: 60);
        var llm = new FakeLlm { Reply = "tóm tắt 20 lượt đầu" };

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        var sut = new ConversationMemoryService(db, llm, new StubPrompts());
        var memory = await sut.LoadAsync(project, _ba, _model);

        Assert.Equal(1, llm.Calls);
        Assert.Equal("tóm tắt 20 lượt đầu", memory.Summary);
        Assert.Equal(ConversationMemoryService.RecentWindowTurns, memory.RecentTurns.Count);
        // Cửa sổ verbatim là PHẦN ĐUÔI: lượt đầu tiên còn lại là lượt thứ 21 (index 20, 0-based).
        Assert.StartsWith("turn-20:", memory.RecentTurns[0].Message);

        // Con trỏ đã được lưu bền.
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == projectId);
        Assert.Equal(20, reloaded.SummarizedTurnCount);
        Assert.Equal("tóm tắt 20 lượt đầu", reloaded.ConversationSummary);
    }

    [Fact]
    public async Task LoadAsync_WhenSummaryCallFails_FailsOpen_KeepsAllTurnsAndNoPointerMove()
    {
        var projectId = await SeedConversationAsync(turns: 60);
        var llm = new FakeLlm { Fail = true };

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        var sut = new ConversationMemoryService(db, llm, new StubPrompts());
        var memory = await sut.LoadAsync(project, _ba, _model);

        Assert.Equal(1, llm.Calls);
        Assert.Null(memory.Summary);
        Assert.Equal(0, project.SummarizedTurnCount);
        // Không gộp được ⇒ KHÔNG mất lượt nào: cả 60 vẫn gửi nguyên văn.
        Assert.Equal(60, memory.RecentTurns.Count);
    }

    [Fact]
    public async Task LoadAsync_FoldsIncrementally_AcrossTwoBatches()
    {
        var projectId = await SeedConversationAsync(turns: 60);
        var llm = new FakeLlm { Reply = "S1" };

        // Lô 1: 60 lượt ⇒ gộp 20, con trỏ = 20.
        await using (var db = NewDb())
        {
            var project = await db.Projects.FirstAsync(p => p.Id == projectId);
            await new ConversationMemoryService(db, llm, new StubPrompts()).LoadAsync(project, _ba, _model);
        }

        // Thêm 20 lượt nữa (tổng 80): chưa gộp 60, thừa 20 ngoài cửa sổ ⇒ đủ ngưỡng, gộp tiếp, con trỏ = 40.
        await AppendTurnsAsync(projectId, from: 60, count: 20);
        llm.Reply = "S2";
        await using (var db = NewDb())
        {
            var project = await db.Projects.FirstAsync(p => p.Id == projectId);
            var memory = await new ConversationMemoryService(db, llm, new StubPrompts()).LoadAsync(project, _ba, _model);
            Assert.Equal("S2", memory.Summary);
            Assert.Equal(40, memory.RecentTurns.Count);
            Assert.StartsWith("turn-40:", memory.RecentTurns[0].Message);
        }

        Assert.Equal(2, llm.Calls);
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == projectId);
        Assert.Equal(40, reloaded.SummarizedTurnCount);
    }

    // ĐÂY LÀ LÝ DO ĐỔI TỪ ĐẾM LƯỢT SANG ĐẾM TOKEN. Mười lượt rất dài (một lượt dán bảng phân quyền dài
    // bằng vài chục lượt gật đầu bằng chip) đã vượt xa trần token của cửa sổ, trong khi luật đếm lượt cũ
    // (cửa sổ 20 + lô 10) sẽ không gộp gì cho tới lượt thứ 30 — tức prompt cứ phình mà không ai chặn.
    [Fact]
    public async Task LoadAsync_FewButVeryLongTurns_StillFold_BecauseWindowIsMeasuredInTokens()
    {
        var projectId = await SeedConversationAsync(turns: 0);
        await AppendTurnsAsync(projectId, from: 0, count: 10, messageChars: 12_000);
        var llm = new FakeLlm { Reply = "S" };

        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        var memory = await new ConversationMemoryService(db, llm, new StubPrompts()).LoadAsync(project, _ba, _model);

        Assert.Equal(1, llm.Calls);
        Assert.Equal("S", memory.Summary);
        // Cửa sổ co lại còn 6 lượt (~18.000 token) — lượt thứ 7 sẽ vượt trần 20.000.
        Assert.Equal(6, memory.RecentTurns.Count);
        Assert.Equal(4, project.SummarizedTurnCount);
    }

    // Một lượt đơn lẻ dài hơn cả trần token KHÔNG được phép làm cửa sổ rỗng: gộp sạch tới lượt cuối là bỏ
    // đi chính câu người dùng vừa nói.
    [Fact]
    public void ComputeFoldableCount_AlwaysKeepsAtLeastOneTurn()
    {
        var giant = new List<AgentConversation>
        {
            new() { Role = "user", Message = new string('x', 400_000) }
        };

        Assert.Equal(0, ConversationMemoryService.ComputeFoldableCount(giant, windowTokens: 20_000));
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

    // messageChars: độ dài mỗi lượt. Mặc định 1.000 ký tự ⇒ ~252 token sau render, đủ để một lô 20 lượt
    // vượt ngưỡng 5.000 token mà 40 lượt vẫn lọt trần token của cửa sổ.
    private async Task AppendTurnsAsync(Guid projectId, int from, int count, int messageChars = 1_000)
    {
        await using var db = NewDb();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = from; i < from + count; i++)
        {
            var prefix = $"turn-{i}:";
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = projectId,
                AgentId = _ba.Id,
                Role = i % 2 == 0 ? "user" : "assistant",
                Message = prefix + new string('x', Math.Max(0, messageChars - prefix.Length)),
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
    }
}
