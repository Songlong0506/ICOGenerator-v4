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

namespace ICOGenerator.Tests.Requirements;

// Bộ nhớ CẤP TOÀN HỆ THỐNG: rút "khoảng trống checklist" (thông tin người dùng tự nêu ra mà BA chưa hỏi)
// từ hội thoại một dự án VỪA hoàn tất, THÊM vào checklist học được cho MỌI dự án MỚI sau này.
// Các test chốt: (1) chưa có hội thoại thì không gọi LLM; (2) dự án đã harvest rồi thì bỏ qua; (3) harvest
// bình thường thì gọi LLM một lần, ghi bài học KÈM LÝ DO + bằng chứng + dự án nguồn, rồi đánh dấu đã
// harvest; (4) lỗi LLM thì fail-open (giữ checklist cũ, không đánh dấu); (5) "không có gì mới" và (6)
// phản hồi không đọc nổi vẫn được coi là xong; (7) KHÔNG học lại bài học người dùng đã tắt.
public class ChecklistGapMemoryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };

    private const string OneLesson = """
        {"items":[{"text":"Hỏi thêm về giới hạn số lần đăng nhập sai.","rationale":"Người dùng tự nêu ràng buộc khóa tài khoản mà BA chưa hỏi nhóm thông tin an toàn đăng nhập.","evidence":"tài khoản phải khóa sau 3 lần sai"}]}
        """;

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
        var (project, _) = await SeedAsync(turns: 0);
        var llm = new FakeLlm();

        await using var db = NewDb();
        var (trackedProject, trackedBa) = await TrackAsync(db, project.Id);
        await NewSut(db, llm).HarvestAsync(trackedProject, trackedBa, _model);

        Assert.Equal(0, llm.Calls);
        Assert.False(trackedProject.ChecklistGapHarvested);
        Assert.Empty(await NewDb().AgentChecklistItems.ToListAsync());
    }

    [Fact]
    public async Task HarvestAsync_AlreadyHarvested_IsSkipped()
    {
        var (project, _) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Reply = "không nên gọi" };

        await using var db = NewDb();
        var (trackedProject, trackedBa) = await TrackAsync(db, project.Id);
        trackedProject.ChecklistGapHarvested = true;

        await NewSut(db, llm).HarvestAsync(trackedProject, trackedBa, _model);

        Assert.Equal(0, llm.Calls);
        Assert.Empty(await NewDb().AgentChecklistItems.ToListAsync());
    }

    [Fact]
    public async Task HarvestAsync_WithConversation_StoresLessonWithReasonAndSource_AndMarksHarvested()
    {
        var (project, _) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Reply = OneLesson };

        await using var db = NewDb();
        var (trackedProject, trackedBa) = await TrackAsync(db, project.Id);
        await NewSut(db, llm).HarvestAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.True(trackedProject.ChecklistGapHarvested);

        var item = await NewDb().AgentChecklistItems.SingleAsync();
        Assert.Equal("Hỏi thêm về giới hạn số lần đăng nhập sai.", item.Text);
        // "Vì sao rút ra được bài học đó" chỉ bắt được TẠI ĐÂY — sau vòng này hội thoại không còn được đọc lại.
        Assert.StartsWith("Người dùng tự nêu", item.Rationale);
        Assert.Equal("tài khoản phải khóa sau 3 lần sai", item.Evidence);
        Assert.Equal(ChecklistItemSource.Conversation, item.SourceKind);
        Assert.Equal(project.Id, item.SourceProjectId);
        Assert.Equal(ChecklistItemStatus.Active, item.Status);
    }

    [Fact]
    public async Task HarvestAsync_ProjectWithDomain_WritesIntoThatBucket()
    {
        var (project, _) = await SeedAsync(turns: 4, domainKey: "leave-management");
        var llm = new FakeLlm { Reply = OneLesson };

        await using var db = NewDb();
        var (trackedProject, trackedBa) = await TrackAsync(db, project.Id);
        await NewSut(db, llm).HarvestAsync(trackedProject, trackedBa, _model);

        Assert.Equal("leave-management", (await NewDb().AgentChecklistItems.SingleAsync()).DomainKey);
    }

    [Fact]
    public async Task HarvestAsync_WhenLlmFails_FailsOpen_KeepsItems_AndDoesNotMarkHarvested()
    {
        var (project, ba) = await SeedAsync(turns: 4);
        await AddItemAsync(ba.Id, "Bài học cũ.", ChecklistItemStatus.Active);
        var llm = new FakeLlm { Fail = true };

        await using var db = NewDb();
        var (trackedProject, trackedBa) = await TrackAsync(db, project.Id);
        await NewSut(db, llm).HarvestAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.False(trackedProject.ChecklistGapHarvested);
        Assert.Equal("Bài học cũ.", (await NewDb().AgentChecklistItems.SingleAsync()).Text);
    }

    [Theory]
    [InlineData("""{"items":[]}""")] // model bảo "không có gì mới"
    [InlineData("xin lỗi, tôi không tìm thấy gì")] // phản hồi không đọc nổi
    public async Task HarvestAsync_NothingUsable_StillMarksHarvested_WithoutAddingItems(string reply)
    {
        var (project, _) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Reply = reply };

        await using var db = NewDb();
        var (trackedProject, trackedBa) = await TrackAsync(db, project.Id);
        await NewSut(db, llm).HarvestAsync(trackedProject, trackedBa, _model);

        Assert.Equal(1, llm.Calls);
        Assert.True(trackedProject.ChecklistGapHarvested);
        Assert.Empty(await NewDb().AgentChecklistItems.ToListAsync());
    }

    // Điểm cốt lõi của việc bỏ blob text: người dùng tắt một bài học sai thì dự án sau lộ lại đúng khoảng
    // trống đó cũng KHÔNG được học lại — trước đây xóa chữ trong ô text xong nó quay về y hệt.
    [Fact]
    public async Task HarvestAsync_DoesNotRelearnLessonUserDisabled()
    {
        var (project, ba) = await SeedAsync(turns: 4);
        await AddItemAsync(ba.Id, "Hỏi thêm về giới hạn số lần đăng nhập sai.", ChecklistItemStatus.DisabledByUser);
        var llm = new FakeLlm { Reply = OneLesson };

        await using var db = NewDb();
        var (trackedProject, trackedBa) = await TrackAsync(db, project.Id);
        await NewSut(db, llm).HarvestAsync(trackedProject, trackedBa, _model);

        var item = await NewDb().AgentChecklistItems.SingleAsync();
        Assert.Equal(ChecklistItemStatus.DisabledByUser, item.Status);

        // Danh sách cấm cũng phải được GỬI cho model, chứ không chỉ chặn ở tầng dữ liệu.
        Assert.Contains("đã bị loại", llm.LastUserMessage);
    }

    private ChecklistGapMemoryService NewSut(AppDbContext db, ILlmClient llm) =>
        new(db, llm, new StubPrompts(), new ChecklistNoteStore(db), NullLogger<ChecklistGapMemoryService>.Instance);

    private static async Task<(Project Project, Agent Ba)> TrackAsync(AppDbContext db, Guid projectId)
    {
        var project = await db.Projects.Include(p => p.Conversations).FirstAsync(p => p.Id == projectId);
        var ba = await db.Agents.FirstAsync();
        return (project, ba);
    }

    private async Task AddItemAsync(Guid agentId, string text, ChecklistItemStatus status)
    {
        await using var db = NewDb();
        db.AgentChecklistItems.Add(new AgentChecklistItem { AgentId = agentId, Text = text, Status = status });
        await db.SaveChangesAsync();
    }

    private async Task<(Project Project, Agent Ba)> SeedAsync(int turns, string? domainKey = null)
    {
        var ba = new Agent { Id = Guid.NewGuid(), Temperature = 0.2, AiModelId = _model.Id };
        var project = new Project { Id = Guid.NewGuid(), Name = "P", DomainKey = domainKey };

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

    // Fake ILlmClient mô phỏng model KHÔNG bật structured output (mặc định): trả text, caller tự parse JSON.
    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public string Reply = """{"items":[]}""";
        public bool Fail;
        public string LastUserMessage = string.Empty;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
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
        public override string Get(string relativePath) => "## rút kinh nghiệm checklist";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
