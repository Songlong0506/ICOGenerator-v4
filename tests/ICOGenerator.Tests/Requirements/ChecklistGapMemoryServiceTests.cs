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

// Bộ nhớ CẤP TOÀN HỆ THỐNG: rút "khoảng trống checklist" (thông tin người dùng tự nêu ra mà BA chưa hỏi)
// từ hội thoại một dự án VỪA hoàn tất, gộp vào Agent.LearnedChecklistNotes cho MỌI dự án MỚI sau này.
// Các test chốt: (1) chưa có hội thoại thì không gọi LLM; (2) dự án đã harvest rồi thì bỏ qua; (3) harvest
// bình thường thì gọi LLM một lần, ghi notes + đánh dấu đã harvest; (4) lỗi LLM thì fail-open (giữ notes cũ,
// không đánh dấu); (5) LLM báo "không có gì mới" (rỗng) vẫn được coi là thành công và đánh dấu đã harvest.
public class ChecklistGapMemoryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };

    public ChecklistGapMemoryServiceTests()
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
    public async Task HarvestAsync_NoConversation_DoesNotCallLlm_OrMarkHarvested()
    {
        var (project, ba) = await SeedAsync(turns: 0);
        var llm = new FakeLlm();

        await using var db = NewDb();
        var trackedProject = await db.Projects.Include(p => p.Conversations).FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).HarvestAsync(trackedProject, trackedBa, _model);

        Assert.Equal(0, llm.Calls);
        Assert.False(trackedProject.ChecklistGapHarvested);
        Assert.Null(trackedBa.LearnedChecklistNotes);
    }

    [Fact]
    public async Task HarvestAsync_AlreadyHarvested_IsSkipped()
    {
        var (project, ba) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Reply = "không nên gọi" };

        await using var db = NewDb();
        var trackedProject = await db.Projects.Include(p => p.Conversations).FirstAsync(p => p.Id == project.Id);
        trackedProject.ChecklistGapHarvested = true;
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).HarvestAsync(trackedProject, trackedBa, _model);

        Assert.Equal(0, llm.Calls);
        Assert.Null(trackedBa.LearnedChecklistNotes);
    }

    [Fact]
    public async Task HarvestAsync_WithConversation_CallsLlmOnce_WritesNotes_AndMarksHarvested()
    {
        var (project, ba) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Reply = "- Hỏi thêm về giới hạn số lần đăng nhập sai." };

        await using var db = NewDb();
        var trackedProject = await db.Projects.Include(p => p.Conversations).FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).HarvestAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.True(trackedProject.ChecklistGapHarvested);
        Assert.Equal("- Hỏi thêm về giới hạn số lần đăng nhập sai.", trackedBa.LearnedChecklistNotes);

        // Bền trong DB, không chỉ trên entity đang track.
        var reloadedBa = await NewDb().Agents.FirstAsync(a => a.Id == ba.Id);
        Assert.Equal("- Hỏi thêm về giới hạn số lần đăng nhập sai.", reloadedBa.LearnedChecklistNotes);
    }

    [Fact]
    public async Task HarvestAsync_WhenLlmFails_FailsOpen_KeepsNotes_AndDoesNotMarkHarvested()
    {
        var (project, ba) = await SeedAsync(turns: 4, existingNotes: "checklist cũ");
        var llm = new FakeLlm { Fail = true };

        await using var db = NewDb();
        var trackedProject = await db.Projects.Include(p => p.Conversations).FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).HarvestAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.False(trackedProject.ChecklistGapHarvested);
        Assert.Equal("checklist cũ", trackedBa.LearnedChecklistNotes);
    }

    [Fact]
    public async Task HarvestAsync_LlmFindsNothingNew_StillMarksHarvested_ClearsNotesIfEmpty()
    {
        var (project, ba) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Reply = "" };

        await using var db = NewDb();
        var trackedProject = await db.Projects.Include(p => p.Conversations).FirstAsync(p => p.Id == project.Id);
        var trackedBa = await db.Agents.FirstAsync(a => a.Id == ba.Id);

        await NewSut(db, llm).HarvestAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.True(trackedProject.ChecklistGapHarvested);
        Assert.Null(trackedBa.LearnedChecklistNotes);
    }

    private ChecklistGapMemoryService NewSut(AppDbContext db, ILlmClient llm) => new(db, llm, new StubPrompts(), new ChecklistNoteStore(db));

    private async Task<(Project Project, Agent Ba)> SeedAsync(int turns, string? existingNotes = null)
    {
        var ba = new Agent { Id = Guid.NewGuid(), Name = "BA", Temperature = 0.2, AiModelId = _model.Id, LearnedChecklistNotes = existingNotes };
        var project = new Project { Id = Guid.NewGuid(), Name = "P" };

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

    // Fake ILlmClient: chỉ phục vụ đường chắt lọc (ChatWithLogAsync). Đếm số lần gọi và trả/đẩy lỗi theo cấu hình.
    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public string Reply = "checklist bổ sung";
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
        public override string Get(string relativePath) => "## rút kinh nghiệm checklist";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
