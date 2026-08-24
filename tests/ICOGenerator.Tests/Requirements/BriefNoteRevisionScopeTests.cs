using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Requirements.Templates;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// PHẠM VI của một ghi chú ghim trên Product Brief. Ca thật đã gặp: người dùng ghi chú ĐÚNG MỘT dòng, mở
// lại tài liệu thì thấy hàng chục dòng khác cũng đổi — vì đường cũ cho ghi chú đi qua nguyên lượt "Write
// Requirement": viết lại cả tài liệu từ transcript, ở temperature > 0, cộng vòng tự soát rà toàn bộ, cộng
// luật truy vết bắt bổ sung mọi điều đã chốt còn thiếu. Không sai một luật nào, nhưng người dùng mất lòng
// tin vào cái nút: ghi chú một dòng mà phải đọc lại cả tài liệu.
//
// Chốt chặn: ghi chú rẽ sang ReviseDraftFromNotesAsync — MỘT lời gọi LLM, bản Brief hiện có là bản gốc,
// KHÔNG cổng readiness, KHÔNG vòng tự soát, KHÔNG khối "Trạng thái đã chắt" (chính khối bắt model đi nhặt
// lại mọi yêu cầu còn thiếu). Các test dưới giữ cho phạm vi đó không nở ra lần nữa.
public class BriefNoteRevisionScopeTests : IDisposable
{
    private const string OriginalBrief = """
        # App nghỉ phép
        ## Sản phẩm này là gì?
        Ứng dụng cho nhân viên gửi đơn nghỉ phép.
        ## Người dùng làm được những gì?
        - Gửi đơn nghỉ phép
          *Hoàn thành khi: quản lý thấy đơn chờ duyệt.*
        - Xem lịch sử đơn
        """;

    private const string RevisedBrief = """
        # App nghỉ phép
        ## Sản phẩm này là gì?
        Ứng dụng cho nhân viên gửi đơn xin nghỉ.
        ## Người dùng làm được những gì?
        - Gửi đơn xin nghỉ
          *Hoàn thành khi: quản lý thấy đơn chờ duyệt.*
        - Xem lịch sử đơn
        """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _workspaceRoot;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public BriefNoteRevisionScopeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "ico-note-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "P",
            Description = "app nghỉ phép",
            // Bản Brief đang có THIẾU hẳn một điều đã chốt. Lượt "Write Requirement" có nhiệm vụ nhặt nó
            // về; lượt sửa theo ghi chú thì KHÔNG — đó là ranh giới đang được test.
            DecisionLog = "- Nhân viên được hủy đơn đã gửi."
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Notes_TakeScopedPath_OneCall_NoSelfReview_NoGate()
    {
        await SeedDraftAsync(OriginalBrief);
        var llm = new FakeLlm { NoteRevision = Result(RevisedBrief, "Đã đổi 'đơn nghỉ phép' thành 'đơn xin nghỉ'.") };

        await using var db = NewDb();
        var outcome = await NewSut(db, llm).ReviseDraftFromNotesAsync(_projectId, new[]
        {
            new BriefNote { Quote = "Gửi đơn nghỉ phép", Note = "gọi là 'đơn xin nghỉ' cho đúng từ công ty dùng" }
        });

        Assert.Equal(RequirementDraftOutcome.Generated, outcome);

        // ĐÚNG MỘT lời gọi LLM. Ba con số 0 dưới đây mới là phần đắt: không lượt soạn lại từ transcript,
        // không vòng tự soát rà toàn tài liệu, không lượt distill bản đồ của cổng readiness.
        Assert.Equal(1, llm.NoteRevisionCalls);
        Assert.Equal(0, llm.ProductBriefCalls);
        Assert.Equal(0, llm.ReviewCalls);
        Assert.Equal(0, llm.CoverageCalls);

        await using var verify = NewDb();
        var doc = await verify.ProjectDocuments.SingleAsync(x => x.ProjectId == _projectId);
        Assert.Equal(RevisedBrief, doc.Content);
    }

    [Fact]
    public async Task ScopedPrompt_HandsOverTheCurrentBriefAndTheNotes_WithoutTheDistilledChecklist()
    {
        await SeedDraftAsync(OriginalBrief);
        var llm = new FakeLlm { NoteRevision = Result(RevisedBrief) };

        await using var db = NewDb();
        await NewSut(db, llm).ReviseDraftFromNotesAsync(_projectId, new[]
        {
            new BriefNote { Quote = "Gửi đơn nghỉ phép", Note = "gọi là 'đơn xin nghỉ'" }
        });

        var prompt = llm.LastUserPrompt!;
        // Bản gốc phải nằm trong prompt — không có nó thì "giữ nguyên phần còn lại" là câu nói suông.
        Assert.Contains(OriginalBrief, prompt, StringComparison.Ordinal);
        Assert.Contains("gọi là 'đơn xin nghỉ'", prompt, StringComparison.Ordinal);
        Assert.Contains("Gửi đơn nghỉ phép", prompt, StringComparison.Ordinal);

        // Khối "Trạng thái đã chắt" là danh sách kiểm bắt model rà lại MỌI điều đã chốt và bổ sung thứ còn
        // thiếu — đúng thứ biến một ghi chú thành một bản Brief đổi hàng chục dòng. Nó phải vắng mặt ở đây,
        // dù dự án có DecisionLog và tài liệu đang thiếu đúng dòng đó.
        Assert.DoesNotContain("Trạng thái đã chắt", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Nhân viên được hủy đơn đã gửi", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoteRevision_IsLabelledInRevisionHistory()
    {
        await SeedDraftAsync(OriginalBrief);
        var llm = new FakeLlm { NoteRevision = Result(RevisedBrief) };

        await using var db = NewDb();
        await NewSut(db, llm).ReviseDraftFromNotesAsync(_projectId, new[]
        {
            new BriefNote { Quote = "Gửi đơn nghỉ phép", Note = "gọi là 'đơn xin nghỉ'" }
        });

        await using var verify = NewDb();
        var revisions = await verify.ProjectDocumentRevisions.OrderBy(x => x.RevisionNumber).ToListAsync();
        // Người dùng mở Lịch sử là phân biệt được ngay bản nào do ghi chú của mình, bản nào do soạn lại.
        Assert.Equal(2, revisions.Count);
        Assert.Contains("ghi chú", revisions[1].ChangeNote, StringComparison.OrdinalIgnoreCase);
    }

    // Không có bản draft nào để giữ nguyên (ghi chú trên bản đã duyệt, file bị xóa) ⇒ rơi về đường soạn
    // đầy đủ thay vì sửa một bản gốc không tồn tại.
    [Fact]
    public async Task NoExistingDraft_FallsBackToFullGeneration()
    {
        await SeedTurnsAsync(
            ("user", "Tôi muốn app quản lý đơn nghỉ phép"),
            ("assistant", "Mình đã đủ thông tin, vui lòng bấm \"Write Requirement\" để tạo tài liệu."));

        // Van "không giả định" của lượt soạn: dừng trước khi ghi file, nhưng vẫn chứng minh đã đi vào
        // đúng lượt soạn tài liệu chứ không phải vòng sửa.
        var llm = new FakeLlm { ProductBrief = new BAProductBriefResult { NeedsClarification = true, ClarifyingQuestion = "Còn thiếu một điểm?" } };

        await using var db = NewDb();
        var outcome = await NewSut(db, llm).ReviseDraftFromNotesAsync(_projectId, new[]
        {
            new BriefNote { Quote = "", Note = "thêm mục báo cáo" }
        });

        Assert.Equal(RequirementDraftOutcome.NeedsMoreInfo, outcome);
        Assert.Equal(1, llm.ProductBriefCalls);
        Assert.Equal(0, llm.NoteRevisionCalls);
    }

    // Bản sửa hỏng/rỗng: tài liệu người dùng đang rà phải còn NGUYÊN, và task phải fail để họ biết ghi chú
    // chưa được áp — im lặng ở đây là kiểu hỏng tệ nhất (tưởng đã sửa, thực ra không).
    [Fact]
    public async Task BrokenRevision_KeepsTheDocumentIntact_AndFailsLoudly()
    {
        await SeedDraftAsync(OriginalBrief);
        var llm = new FakeLlm { NoteRevision = Result("") };

        await using var db = NewDb();
        var sut = NewSut(db, llm);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ReviseDraftFromNotesAsync(_projectId, new[]
        {
            new BriefNote { Quote = "Gửi đơn nghỉ phép", Note = "gọi là 'đơn xin nghỉ'" }
        }));

        await using var verify = NewDb();
        Assert.Equal(OriginalBrief, (await verify.ProjectDocuments.SingleAsync()).Content);
        Assert.Equal(1, await verify.ProjectDocumentRevisions.CountAsync());
    }

    // Ghi chú không hiểu nổi / mâu thuẫn với điều đã chốt ⇒ hỏi lại trong khung chat, KHÔNG đụng tài liệu.
    [Fact]
    public async Task UnclearNote_AsksInChat_AndLeavesTheDocumentAlone()
    {
        await SeedDraftAsync(OriginalBrief);
        var llm = new FakeLlm
        {
            NoteRevision = new BAProductBriefResult
            {
                NeedsClarification = true,
                ClarifyingQuestion = "Ý anh/chị là bỏ hẳn tính năng này hay chỉ đổi tên?"
            }
        };

        await using var db = NewDb();
        var outcome = await NewSut(db, llm).ReviseDraftFromNotesAsync(_projectId, new[]
        {
            new BriefNote { Quote = "Xem lịch sử đơn", Note = "cái này không cần" }
        });

        Assert.Equal(RequirementDraftOutcome.NeedsMoreInfo, outcome);

        await using var verify = NewDb();
        Assert.Equal(OriginalBrief, (await verify.ProjectDocuments.SingleAsync()).Content);
        var lastTurn = await verify.AgentConversations
            .Where(c => c.ProjectId == _projectId && c.Role == "assistant")
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .LastAsync();
        Assert.Contains("bỏ hẳn tính năng này", lastTurn.Message, StringComparison.Ordinal);
    }

    // ---------- Luật trong file prompt thật ----------

    [Fact]
    public void NoteRevisionPrompt_ForbidsRewritingWhatWasNotNoted()
    {
        var prompt = CoveragePromptFixture.ReadPrompt("BusinessAnalyst/product-brief-note-revision.v1.md");

        // Ba luật gánh toàn bộ giá trị của lượt này. Mất luật nào thì "sửa có phạm vi" chỉ còn là cái tên.
        Assert.Contains("CHÉP NGUYÊN VĂN", prompt, StringComparison.Ordinal);
        Assert.Contains("KHÔNG tự bổ sung yêu cầu từ hội thoại", prompt, StringComparison.Ordinal);
        Assert.Contains("KHÔNG tự giả định", prompt, StringComparison.Ordinal);
    }

    // ---------- Hạ tầng test ----------

    private static BAProductBriefResult Result(string content, string assistantMessage = "Đã sửa theo ghi chú.") =>
        new()
        {
            AssistantMessage = assistantMessage,
            ProductBrief = new ProductBriefDto { Content = content }
        };

    private async Task SeedDraftAsync(string content)
    {
        await using var db = NewDb();
        var project = await db.Projects.FirstAsync(x => x.Id == _projectId);
        await NewGenerator(db).GenerateProductBriefDraftFiles(project, _baId, Result(content));
        await db.SaveChangesAsync();
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

    private IConfiguration NewConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _workspaceRoot })
        .Build();

    private RequirementDocumentGenerator NewGenerator(AppDbContext db)
    {
        var resolver = new WorkspacePathResolver(NewConfig());
        return new RequirementDocumentGenerator(
            db,
            new RequirementTemplateService(new FakeWebHostEnvironment()),
            new DocxTemplateWriter(),
            resolver,
            new ProjectArtifactCatalog(),
            new LocalArtifactStorage(resolver, NullLogger<LocalArtifactStorage>.Instance));
    }

    private ProductBriefDraftService NewSut(AppDbContext db, ILlmClient llm)
    {
        var config = NewConfig();
        var prompts = new StubPrompts();
        var catalog = new ProjectArtifactCatalog();
        return new ProductBriefDraftService(
            db,
            llm,
            new RequirementPromptBuilder(),
            new RequirementResponseParser(),
            NewGenerator(db),
            prompts,
            new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance),
            catalog,
            new ChecklistGapMemoryService(db, llm, prompts, new ChecklistNoteStore(db), NullLogger<ChecklistGapMemoryService>.Instance),
            new ProductBriefReviewParser(),
            new OrganizationContextService(db, prompts, new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new RequirementCoverageService(db, llm, prompts),
            new BAAgentResolver(db),
            new BAConversationLog(db));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_workspaceRoot, true); } catch { /* dọn tạm, lỗi bỏ qua */ }
    }

    // Đếm RIÊNG từng purpose: giá trị của bài test nằm ở các con số 0 (không soạn lại, không tự soát).
    private sealed class FakeLlm : ILlmClient
    {
        public BAProductBriefResult? NoteRevision;
        public BAProductBriefResult? ProductBrief;
        public int NoteRevisionCalls;
        public int ProductBriefCalls;
        public int ReviewCalls;
        public int CoverageCalls;
        public string? LastUserPrompt;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            if (logContext.Purpose == "BARequirementCoverage")
                CoverageCalls++;
            return Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "fail-open path in tests" });
        }

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            LastUserPrompt = messages.Last().Text;

            object? value;
            switch (logContext.Purpose)
            {
                case "BAProductBriefNoteRevision":
                    NoteRevisionCalls++;
                    value = NoteRevision;
                    break;
                case "BAProductBrief":
                    ProductBriefCalls++;
                    value = ProductBrief;
                    break;
                case "BAProductBriefReview":
                case "BAProductBriefRevision":
                    ReviewCalls++;
                    value = null;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected structured call: {logContext.Purpose}");
            }

            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)value));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
