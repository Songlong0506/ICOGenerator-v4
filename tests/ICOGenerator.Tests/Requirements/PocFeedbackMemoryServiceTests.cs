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
using ICOGenerator.Tests;

namespace ICOGenerator.Tests.Requirements;

// Đóng vòng học từ ghi chú POC → checklist học được của BA, chạy ở mốc người dùng DUYỆT bản demo. Các
// test chốt: (1) chưa duyệt (cờ tắt) thì không gọi LLM dù đã có ghi chú; (2) duyệt mà không ghi chú nào
// thì cũng không gọi LLM, chỉ hạ cờ; (3) harvest bình thường ghi bài học KÈM LÝ DO + nguồn, dời con trỏ
// và hạ cờ; (4) lỗi LLM thì fail-open (giữ bài học cũ, con trỏ + cờ đứng yên); (5) lần duyệt sau chỉ gộp
// ghi chú MỚI kể từ con trỏ — kể cả khi trạng thái ghi chú cũ đã đổi (Sent → Addressed).
public class PocFeedbackMemoryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };

    private const string OneLesson = """
        {"items":[{"text":"Hỏi đủ các cột của bảng tính tiền.","rationale":"POC thiếu cột phụ cấp vì phỏng vấn không hỏi hết các khoản thành phần của bảng tính.","evidence":"bảng lương thiếu cột phụ cấp"}]}
        """;

    public PocFeedbackMemoryServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.SaveChanges();
    }

    // Bản demo chưa được duyệt: ghi chú đã có nhưng chưa phải tập ĐÃ ĐÓNG — vòng sửa kế tiếp còn đổi được.
    [Fact]
    public async Task TryHarvestAsync_NotApprovedYet_DoesNotCallLlm()
    {
        var project = await SeedAsync(sentComments: 3, openComments: 1, pendingHarvest: false);
        var llm = new FakeLlm();

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(0, llm.Calls);
        Assert.Equal(0, (await NewDb().Projects.FirstAsync(p => p.Id == project.Id)).PocFeedbackHarvestedCount);
    }

    // Duyệt thẳng bản demo, không ghi chú nào ⇒ không có gì để học: hạ cờ, KHÔNG tốn một lời gọi LLM.
    [Fact]
    public async Task TryHarvestAsync_ApprovedWithoutAnyComment_DoesNotCallLlm_AndClearsFlag()
    {
        var project = await SeedAsync(sentComments: 0, openComments: 0);
        var llm = new FakeLlm();

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(0, llm.Calls);
        Assert.False((await NewDb().Projects.FirstAsync(p => p.Id == project.Id)).PendingPocFeedbackHarvest);
    }

    // Ghi chú đã thu hồi không phải bằng chứng — người ghim đã tự rút lại lời chê.
    [Fact]
    public async Task TryHarvestAsync_OnlyWithdrawnComments_DoesNotCallLlm_ButStillAdvancesCursor()
    {
        var project = await SeedAsync(sentComments: 0, openComments: 0, withdrawnComments: 2);
        var llm = new FakeLlm();

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(0, llm.Calls);
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Equal(2, reloaded.PocFeedbackHarvestedCount);
        Assert.False(reloaded.PendingPocFeedbackHarvest);
    }

    [Fact]
    public async Task TryHarvestAsync_WithComments_StoresLessonWithReason_AndAdvancesCursor()
    {
        var project = await SeedAsync(sentComments: 3, openComments: 1);
        var llm = new FakeLlm { Reply = OneLesson };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(1, llm.Calls);
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        // Con trỏ đếm MỌI ghi chú POC đã cân nhắc (4), không riêng ghi chú đã gửi cho Developer: ghi chú
        // còn Open lúc duyệt vẫn là điều bản demo làm người dùng phải gõ ra.
        Assert.Equal(4, reloaded.PocFeedbackHarvestedCount);
        Assert.False(reloaded.PendingPocFeedbackHarvest);
        Assert.Contains("ghi chú open 0", llm.LastUserMessage);

        var item = await NewDb().AgentChecklistItems.SingleAsync();
        Assert.Equal("Hỏi đủ các cột của bảng tính tiền.", item.Text);
        Assert.StartsWith("POC thiếu cột phụ cấp", item.Rationale);
        Assert.Equal("bảng lương thiếu cột phụ cấp", item.Evidence);
        Assert.Equal(ChecklistItemSource.PocFeedback, item.SourceKind);
        Assert.Equal(project.Id, item.SourceProjectId);
    }

    [Fact]
    public async Task TryHarvestAsync_LlmFails_FailsOpen()
    {
        var project = await SeedAsync(sentComments: 2, openComments: 0, existingLesson: "Bài học cũ.");
        var llm = new FakeLlm { Fail = true };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(1, llm.Calls);
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Equal(0, reloaded.PocFeedbackHarvestedCount);
        // Cờ ĐỨNG YÊN cùng con trỏ: task sau gộp bù, chứ không mất trắng bằng chứng của một lần duyệt.
        Assert.True(reloaded.PendingPocFeedbackHarvest);
        Assert.Equal("Bài học cũ.", (await NewDb().AgentChecklistItems.SingleAsync()).Text);
    }

    // Lần duyệt SAU chỉ gộp ghi chú mới — và phải đúng cả khi trạng thái ghi chú cũ đã đổi trong lúc đó
    // (Sent → Addressed sau vòng sửa, hoặc Addressed → Open khi người review mở lại). Con trỏ đếm theo tập
    // ĐÃ LỌC trạng thái sẽ nhảy qua mất ghi chú mới ở đúng tình huống này.
    [Fact]
    public async Task TryHarvestAsync_SecondApproval_OnlyDistillsNewComments_EvenAfterStatusChanged()
    {
        var project = await SeedAsync(sentComments: 2, openComments: 0);
        var llm = new FakeLlm { Reply = OneLesson };

        await using (var db = NewDb())
        {
            await NewSut(db, llm).TryHarvestAsync(project.Id);
        }

        // Vòng sửa đóng các ghi chú cũ lại, người dùng ghim thêm một ghi chú rồi duyệt lần nữa.
        await using (var db = NewDb())
        {
            foreach (var old in await db.PocComments.Where(c => c.ProjectId == project.Id).ToListAsync())
                old.Status = PocCommentStatus.Addressed;
            db.PocComments.Add(NewComment(project.Id, "ghi chú mới nhất", PocCommentStatus.Sent, offsetSeconds: 100));
            (await db.Projects.FirstAsync(p => p.Id == project.Id)).PendingPocFeedbackHarvest = true;
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            await NewSut(db, llm).TryHarvestAsync(project.Id);
        }

        Assert.Equal(2, llm.Calls);
        Assert.Contains("ghi chú mới nhất", llm.LastUserMessage);
        Assert.DoesNotContain("ghi chú cũ 0", llm.LastUserMessage);
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Equal(3, reloaded.PocFeedbackHarvestedCount);
    }

    // Ghi chú Brief đi đường riêng (ChecklistGapMemoryService, ở mốc duyệt Brief) — đếm nó vào con trỏ này
    // là đẩy con trỏ vượt quá và bỏ qua bài học của những lần duyệt POC sau.
    [Fact]
    public async Task TryHarvestAsync_IgnoresBriefNotes()
    {
        var project = await SeedAsync(sentComments: 1, openComments: 0);
        await using (var seed = NewDb())
        {
            var brief = NewComment(project.Id, "ghi chú trên bản mô tả", PocCommentStatus.RoutedToRequirement, offsetSeconds: 5);
            brief.Target = PocCommentTarget.Brief;
            seed.PocComments.Add(brief);
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm { Reply = OneLesson };
        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.DoesNotContain("ghi chú trên bản mô tả", llm.LastUserMessage);
        Assert.Equal(1, (await NewDb().Projects.FirstAsync(p => p.Id == project.Id)).PocFeedbackHarvestedCount);
    }

    private PocFeedbackMemoryService NewSut(AppDbContext db, ILlmClient llm) =>
        new(db, llm, new StubPrompts(), new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)), NullLogger<PocFeedbackMemoryService>.Instance);

    private async Task<Project> SeedAsync(
        int sentComments,
        int openComments,
        string? existingLesson = null,
        int withdrawnComments = 0,
        bool pendingHarvest = true)
    {
        var ba = new Agent
        {
            Id = Guid.NewGuid(),
            RoleKey = AgentRoleKey.BusinessAnalyst,
            Temperature = 0.2,
            AiModelId = _model.Id
        };
        var project = new Project { Id = Guid.NewGuid(), Name = "P", PendingPocFeedbackHarvest = pendingHarvest };

        await using var db = NewDb();
        db.Agents.Add(ba);
        db.Projects.Add(project);
        if (existingLesson != null)
            db.AgentChecklistItems.Add(new AgentChecklistItem { AgentId = ba.Id, Text = existingLesson });
        for (var i = 0; i < sentComments; i++)
            db.PocComments.Add(NewComment(project.Id, $"ghi chú cũ {i}", PocCommentStatus.Sent, i));
        for (var i = 0; i < openComments; i++)
            db.PocComments.Add(NewComment(project.Id, $"ghi chú open {i}", PocCommentStatus.Open, 50 + i));
        for (var i = 0; i < withdrawnComments; i++)
        {
            var withdrawn = NewComment(project.Id, $"ghi chú đã thu hồi {i}", PocCommentStatus.Open, 80 + i);
            withdrawn.WithdrawnAtUtc = DateTime.UtcNow;
            db.PocComments.Add(withdrawn);
        }
        await db.SaveChangesAsync();
        return project;
    }

    private static PocComment NewComment(Guid projectId, string comment, PocCommentStatus status, int offsetSeconds) => new()
    {
        ProjectId = projectId,
        PageView = "Danh sách",
        ElementLabel = "Bảng",
        Comment = comment,
        Status = status,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(offsetSeconds)
    };

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public string Reply = OneLesson;
        public bool Fail;
        public string LastUserMessage = string.Empty;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastUserMessage = messages.Last().Text ?? string.Empty;
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
        public override string Get(string relativePath) => "## rút kinh nghiệm từ ghi chú POC";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
