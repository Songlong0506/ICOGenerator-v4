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

// Bộ nhớ CẤP TOÀN HỆ THỐNG: rút bài học cho BỘ CÂU HỎI của BA ở mốc người dùng DUYỆT Product Brief, rồi
// THÊM vào checklist học được cho MỌI dự án MỚI sau này. Hai nhánh: có ghi chú trên bản vừa duyệt ⇒ học từ
// ghi chú (bằng chứng trực tiếp, chạy lại ở mỗi bản có ghi chú); không ghi chú ⇒ lưới đỡ rà hội thoại
// (bằng chứng gián tiếp, đúng MỘT lần cho cả đời dự án).
// Các test chốt: (1) chưa duyệt thì không gọi LLM; (2) có ghi chú ⇒ ghi chú vào prompt, nguồn BriefNote;
// (3) không ghi chú ⇒ lưới đỡ chạy, nguồn Conversation, đánh dấu đã rà; (4) lưới đỡ không chạy lần hai;
// (5) lỗi LLM thì fail-open (giữ checklist cũ + hàng đợi); (6) "không có gì mới"/không đọc nổi vẫn là
// xong; (7) KHÔNG học lại bài học người dùng đã tắt; (8) bucket theo phòng ban.
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

    // Bản nháp vừa sinh xong nhưng chưa ai duyệt: chưa có bằng chứng nào về việc bộ câu hỏi thiếu gì.
    [Fact]
    public async Task TryHarvestAsync_BriefNotApprovedYet_DoesNotCallLlm()
    {
        var (project, _) = await SeedAsync(turns: 4, approvedVersion: null);
        var llm = new FakeLlm();

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(0, llm.Calls);
        Assert.Empty(await NewDb().AgentChecklistItems.ToListAsync());
    }

    [Fact]
    public async Task TryHarvestAsync_NoConversationAndNoNote_DoesNotCallLlm_ButClearsQueue()
    {
        var (project, _) = await SeedAsync(turns: 0);
        var llm = new FakeLlm();

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(0, llm.Calls);
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Null(reloaded.PendingChecklistHarvestVersion);
        Assert.False(reloaded.ChecklistGapHarvested);
    }

    // ĐƯỜNG SẮC: người dùng ghim ghi chú lên bản nháp trước khi duyệt ⇒ ghi chú là bằng chứng chính, và
    // bài học truy nguồn về BriefNote chứ không phải Conversation.
    [Fact]
    public async Task TryHarvestAsync_WithBriefNotes_SendsNotesToPrompt_AndTagsSourceBriefNote()
    {
        var (project, _) = await SeedAsync(turns: 4, notes: ["thiếu mất quy tắc khoá tài khoản"]);
        var llm = new FakeLlm { Reply = OneLesson };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(1, llm.Calls);
        Assert.Contains("thiếu mất quy tắc khoá tài khoản", llm.LastUserMessage);
        // Hội thoại vẫn đi kèm: ghi chú nói CÁI GÌ thiếu, transcript nói BA đã hỏi tới đâu.
        Assert.Contains("turn-0", llm.LastUserMessage);

        var item = await NewDb().AgentChecklistItems.SingleAsync();
        Assert.Equal("Hỏi thêm về giới hạn số lần đăng nhập sai.", item.Text);
        // "Vì sao rút ra được bài học đó" chỉ bắt được TẠI ĐÂY — sau vòng này ghi chú không còn được đọc lại.
        Assert.StartsWith("Người dùng tự nêu", item.Rationale);
        Assert.Equal("tài khoản phải khóa sau 3 lần sai", item.Evidence);
        Assert.Equal(ChecklistItemSource.BriefNote, item.SourceKind);
        Assert.Equal(project.Id, item.SourceProjectId);
        Assert.Equal(ChecklistItemStatus.Active, item.Status);

        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.Null(reloaded.PendingChecklistHarvestVersion);
        // Lưới đỡ CHƯA tiêu: bản sau không ghi chú gì thì vẫn còn quyền rà transcript một lần.
        Assert.False(reloaded.ChecklistGapHarvested);
    }

    // Ghi chú của bản KHÁC không tính: chúng thuộc một lần duyệt đã học rồi.
    [Fact]
    public async Task TryHarvestAsync_IgnoresNotesFromOtherBriefVersions()
    {
        var (project, _) = await SeedAsync(turns: 4, approvedVersion: "V2", notes: ["ghi chú của V2"]);
        await AddBriefNoteAsync(project.Id, "ghi chú của V1", "V1");
        var llm = new FakeLlm { Reply = OneLesson };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Contains("ghi chú của V2", llm.LastUserMessage);
        Assert.DoesNotContain("ghi chú của V1", llm.LastUserMessage);
    }

    // Ghi chú đã thu hồi không phải bằng chứng — người ghim đã tự rút lại lời chê, nên bản này coi như
    // không có ghi chú và rơi về lưới đỡ.
    [Fact]
    public async Task TryHarvestAsync_WithdrawnNote_FallsBackToConversationNet()
    {
        var (project, _) = await SeedAsync(turns: 4);
        await AddBriefNoteAsync(project.Id, "ghi chú đã rút lại", "V1", withdrawn: true);
        var llm = new FakeLlm { Reply = OneLesson };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.DoesNotContain("ghi chú đã rút lại", llm.LastUserMessage);
        Assert.Equal(ChecklistItemSource.Conversation, (await NewDb().AgentChecklistItems.SingleAsync()).SourceKind);
    }

    // LƯỚI ĐỠ: duyệt mà không ghi chú gì vẫn rà hội thoại — Brief đúng ngay có thể chỉ vì người dùng đã tự
    // khai đủ phần BA quên hỏi, và người dùng sau sẽ không chủ động như vậy.
    [Fact]
    public async Task TryHarvestAsync_NoNotes_StillHarvestsConversation_AndMarksHarvested()
    {
        var (project, _) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Reply = OneLesson };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(1, llm.Calls);
        Assert.Equal(ChecklistItemSource.Conversation, (await NewDb().AgentChecklistItems.SingleAsync()).SourceKind);

        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.True(reloaded.ChecklistGapHarvested);
        Assert.Null(reloaded.PendingChecklistHarvestVersion);
    }

    // Lưới đỡ chỉ đáng MỘT lời gọi cho cả đời dự án: bản duyệt sau, cũng không ghi chú, đọc lại đúng
    // transcript đó thì chỉ tốn thêm chứ không khá hơn.
    [Fact]
    public async Task TryHarvestAsync_SecondApprovalWithoutNotes_DoesNotRunNetAgain()
    {
        var (project, _) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Reply = OneLesson };

        await using (var db = NewDb())
            await NewSut(db, llm).TryHarvestAsync(project.Id);

        await using (var db = NewDb())
        {
            (await db.Projects.FirstAsync(p => p.Id == project.Id)).PendingChecklistHarvestVersion = "V2";
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
            await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(1, llm.Calls);
        Assert.Null((await NewDb().Projects.FirstAsync(p => p.Id == project.Id)).PendingChecklistHarvestVersion);
    }

    // …nhưng bản duyệt sau CÓ ghi chú thì vẫn học: đó là bằng chứng mới, không phải transcript cũ.
    [Fact]
    public async Task TryHarvestAsync_SecondApprovalWithNotes_RunsAgain()
    {
        var (project, _) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Reply = OneLesson };

        await using (var db = NewDb())
            await NewSut(db, llm).TryHarvestAsync(project.Id);

        await AddBriefNoteAsync(project.Id, "bản V2 vẫn thiếu hạn mức duyệt", "V2");
        await using (var db = NewDb())
        {
            (await db.Projects.FirstAsync(p => p.Id == project.Id)).PendingChecklistHarvestVersion = "V2";
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
            await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(2, llm.Calls);
        Assert.Contains("bản V2 vẫn thiếu hạn mức duyệt", llm.LastUserMessage);
    }

    // Dự án gắn orgUnit CON ⇒ bài học rơi vào bucket của DEPARTMENT cha, không phải mã orgUnit đó: 195
    // orgUnit mà chỉ 15 department, gom theo lá thì bucket nào cũng lèo tèo vài mục.
    [Fact]
    public async Task TryHarvestAsync_ProjectUnderDepartment_WritesIntoDepartmentBucket()
    {
        var (project, _) = await SeedAsync(turns: 4, orgUnitCode: "50101");
        var llm = new FakeLlm { Reply = OneLesson };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal("50100", (await NewDb().AgentChecklistItems.SingleAsync()).DepartmentCode);
    }

    // OrgUnit không thuộc department nào (dữ liệu HR đứt đoạn) ⇒ bucket CHUNG, chứ không tự đẻ ra một
    // bucket riêng cho orgUnit đó.
    [Fact]
    public async Task TryHarvestAsync_OrphanOrgUnit_FallsBackToCommonBucket()
    {
        var (project, _) = await SeedAsync(turns: 4, orgUnitCode: "50999");
        var llm = new FakeLlm { Reply = OneLesson };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Null((await NewDb().AgentChecklistItems.SingleAsync()).DepartmentCode);
    }

    [Fact]
    public async Task TryHarvestAsync_WhenLlmFails_FailsOpen_KeepsItems_AndKeepsQueue()
    {
        var (project, ba) = await SeedAsync(turns: 4);
        await AddItemAsync(ba.Id, "Bài học cũ.", ChecklistItemStatus.Active);
        var llm = new FakeLlm { Fail = true };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(1, llm.Calls);
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.False(reloaded.ChecklistGapHarvested);
        // Hàng đợi ĐỨNG YÊN: task sau gộp bù thay vì mất trắng bằng chứng của lần duyệt này.
        Assert.Equal("V1", reloaded.PendingChecklistHarvestVersion);
        Assert.Equal("Bài học cũ.", (await NewDb().AgentChecklistItems.SingleAsync()).Text);
    }

    [Theory]
    [InlineData("""{"items":[]}""")] // model bảo "không có gì mới"
    [InlineData("xin lỗi, tôi không tìm thấy gì")] // phản hồi không đọc nổi
    public async Task TryHarvestAsync_NothingUsable_StillClearsQueue_WithoutAddingItems(string reply)
    {
        var (project, _) = await SeedAsync(turns: 4);
        var llm = new FakeLlm { Reply = reply };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        Assert.Equal(1, llm.Calls);
        var reloaded = await NewDb().Projects.FirstAsync(p => p.Id == project.Id);
        Assert.True(reloaded.ChecklistGapHarvested);
        Assert.Null(reloaded.PendingChecklistHarvestVersion);
        Assert.Empty(await NewDb().AgentChecklistItems.ToListAsync());
    }

    // Điểm cốt lõi của việc bỏ blob text: người dùng tắt một bài học sai thì dự án sau lộ lại đúng khoảng
    // trống đó cũng KHÔNG được học lại — trước đây xóa chữ trong ô text xong nó quay về y hệt.
    [Fact]
    public async Task TryHarvestAsync_DoesNotRelearnLessonUserDisabled()
    {
        var (project, ba) = await SeedAsync(turns: 4);
        await AddItemAsync(ba.Id, "Hỏi thêm về giới hạn số lần đăng nhập sai.", ChecklistItemStatus.DisabledByUser);
        var llm = new FakeLlm { Reply = OneLesson };

        await using var db = NewDb();
        await NewSut(db, llm).TryHarvestAsync(project.Id);

        var item = await NewDb().AgentChecklistItems.SingleAsync();
        Assert.Equal(ChecklistItemStatus.DisabledByUser, item.Status);

        // Danh sách cấm cũng phải được GỬI cho model, chứ không chỉ chặn ở tầng dữ liệu.
        Assert.Contains("đã bị loại", llm.LastUserMessage);
    }

    private ChecklistGapMemoryService NewSut(AppDbContext db, ILlmClient llm) =>
        new(db, llm, new StubPrompts(), new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)), NullLogger<ChecklistGapMemoryService>.Instance);

    private async Task AddItemAsync(Guid agentId, string text, ChecklistItemStatus status)
    {
        await using var db = NewDb();
        db.AgentChecklistItems.Add(new AgentChecklistItem { AgentId = agentId, Text = text, Status = status });
        await db.SaveChangesAsync();
    }

    private async Task AddBriefNoteAsync(Guid projectId, string comment, string briefVersion, bool withdrawn = false)
    {
        await using var db = NewDb();
        db.PocComments.Add(new PocComment
        {
            ProjectId = projectId,
            Target = PocCommentTarget.Brief,
            BriefVersion = briefVersion,
            Comment = comment,
            Status = PocCommentStatus.RoutedToRequirement,
            WithdrawnAtUtc = withdrawn ? DateTime.UtcNow : null,
            CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();
    }

    private async Task<(Project Project, Agent Ba)> SeedAsync(
        int turns,
        string? orgUnitCode = null,
        string? approvedVersion = "V1",
        IReadOnlyList<string>? notes = null)
    {
        var ba = new Agent
        {
            Id = Guid.NewGuid(),
            RoleKey = AgentRoleKey.BusinessAnalyst,
            Temperature = 0.2,
            AiModelId = _model.Id
        };
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OrgUnitCode = orgUnitCode,
            PendingChecklistHarvestVersion = approvedVersion
        };

        await using var db = NewDb();
        db.Agents.Add(ba);
        // Cây tổ chức tối thiểu: 50101 (orgUnit con) → 50100 (department). 50999 cố tình đứng lẻ.
        db.OrgUnits.Add(new OrgUnit { Id = Guid.NewGuid(), OrgUnitCode = "50100", DisplayName = "HcP/HRL", IsDepartment = true });
        db.OrgUnits.Add(new OrgUnit { Id = Guid.NewGuid(), OrgUnitCode = "50101", DisplayName = "HcP/HRL1", TargetResponsible = "50100" });
        db.OrgUnits.Add(new OrgUnit { Id = Guid.NewGuid(), OrgUnitCode = "50999", DisplayName = "HcP/LONE" });
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

        foreach (var note in notes ?? Array.Empty<string>())
            await AddBriefNoteAsync(project.Id, note, approvedVersion ?? "V1");

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
    }
}
