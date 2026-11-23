using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Organization;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;
using ICOGenerator.Tests;

namespace ICOGenerator.Tests.Requirements;

// CHẾ ĐỘ YÊU CẦU THAY ĐỔI — sau khi Product Brief đã duyệt, lượt chat BA không còn là buổi phỏng vấn.
//
// Ca thật (log BAChat 2026-11-20, sau POC người dùng xin thêm màn hình Tòa nhà/Khu vực): prompt 97k token
// gồm prompt phỏng vấn ~100.000 ký tự, bản đồ bao phủ, bốn bảng đã chốt, bối cảnh toàn nhà máy, thống kê
// cột của file Excel và CẢ buổi phỏng vấn nguyên văn — trong khi bản Brief đã duyệt, thứ duy nhất nói được
// "điều này đã có chưa", thì vắng mặt. Các test chốt hình dạng ngữ cảnh mới.
public class BAChatChangeRequestModeTests : IDisposable
{
    private const string BriefText = "BRIEF-V1: Locker List quản lý tủ theo khu và tòa nhà.";
    private const string OldSourceText = "THỐNG-KÊ-CỘT-CỦA-FILE-EXCEL-CŨ";
    private const string NewSourceText = "FILE-MỚI-SAU-MỐC-DUYỆT";
    private const string PreMarkMarker = "LƯỢT-PHỎNG-VẤN";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "gpt-5.6-luna", ContextWindow = 1_050_000 };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();
    private readonly DateTime _t0 = new(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc);

    public BAChatChangeRequestModeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = _model.Id });
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Locker",
            UxBlueprint = UxBlueprintBuilder.Serialize(new UxBlueprint
            {
                Screens =
                {
                    new UxScreen { Id = "S-01", Name = "Locker List", Purpose = "Quản lý tủ" },
                    new UxScreen { Id = "S-02", Name = "Ticket Approval", Purpose = "Duyệt ticket" }
                }
            }),
        });

        // Buổi phỏng vấn: 6 lượt trước mốc duyệt, file Excel đọc ở giữa buổi.
        for (var i = 0; i < 6; i++)
        {
            db.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId,
                AgentId = _baId,
                Role = i % 2 == 0 ? "user" : "assistant",
                Message = $"{PreMarkMarker}-{i}",
                CreatedAt = _t0.AddMinutes(i),
            });
        }
        db.ProjectSourceFiles.Add(new ProjectSourceFile
        {
            ProjectId = _projectId,
            Kind = SourceFileKind.Document,
            FileName = "locker.xlsx",
            ExtractedText = OldSourceText,
            CreatedAt = _t0.AddMinutes(1).AddSeconds(30),
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task AfterApproval_SendsTheChangePromptAndTheApprovedBrief_NotTheInterviewContext()
    {
        await ApproveAsync();

        var llm = new FakeLlm();
        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Thêm màn hình quản lý Tòa nhà và Khu vực");

        Assert.Equal(ChatWithBAResult.Ok, result.Status);
        var texts = llm.LastMessages.Select(m => m.Text ?? string.Empty).ToList();

        Assert.Equal("## prompt " + BAChatService.ChangeRequestPromptKey, texts[0]);
        Assert.DoesNotContain(texts, t => t.Contains("requirement-chat.v4", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("## Product Brief đã duyệt (V1", StringComparison.Ordinal) && t.Contains(BriefText));
        Assert.Contains(texts, t => t.Contains("* Locker List — Quản lý tủ", StringComparison.Ordinal));

        // Bản đồ bao phủ và các bảng đã chốt không đi kèm: Brief đã chở nội dung của chúng.
        Assert.DoesNotContain(texts, t => t.StartsWith("## Bản đồ bao phủ", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("Phạm vi màn hình đã CHỐT", StringComparison.Ordinal));

        // Phần phỏng vấn trước mốc đi dưới dạng TÓM TẮT, không nguyên văn; file đọc ở buổi phỏng vấn cũng không.
        Assert.Contains(texts, t => t.StartsWith("## Bộ nhớ hội thoại", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains(PreMarkMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains(OldSourceText, StringComparison.Ordinal));
        Assert.Equal("Thêm màn hình quản lý Tòa nhà và Khu vực", texts[^1]);

        await using var check = NewDb();
        Assert.Equal(6, (await check.Projects.FirstAsync(p => p.Id == _projectId)).SummarizedTurnCount);
    }

    // File người dùng tải lên SAU mốc duyệt thì Brief chưa hề thấy ⇒ vẫn phải đi kèm.
    [Fact]
    public async Task AfterApproval_SourcesUploadedAfterTheMark_StillGoAlong()
    {
        await ApproveAsync();
        await using (var seed = NewDb())
        {
            seed.ProjectSourceFiles.Add(new ProjectSourceFile
            {
                ProjectId = _projectId,
                Kind = SourceFileKind.Document,
                FileName = "building.xlsx",
                ExtractedText = NewSourceText,
                CreatedAt = _t0.AddHours(2),
            });
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm();
        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "Đây là danh sách tòa nhà");

        var texts = llm.LastMessages.Select(m => m.Text ?? string.Empty).ToList();
        Assert.Contains(texts, t => t.Contains(NewSourceText, StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains(OldSourceText, StringComparison.Ordinal));
    }

    // Gộp tóm tắt lỗi ⇒ fail-open: vẫn chế độ thay đổi, nhưng phần trước mốc đi nguyên văn — không mất gì.
    [Fact]
    public async Task AfterApproval_WhenTheFoldFails_TheInterviewTurnsStayVerbatim()
    {
        await ApproveAsync();

        var llm = new FakeLlm { SummaryFails = true };
        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "Thêm màn hình Khu vực");

        var texts = llm.LastMessages.Select(m => m.Text ?? string.Empty).ToList();
        Assert.Equal("## prompt " + BAChatService.ChangeRequestPromptKey, texts[0]);
        Assert.Contains(texts, t => t.Contains(PreMarkMarker + "-0", StringComparison.Ordinal));
    }

    // Chưa duyệt Brief ⇒ buổi phỏng vấn như cũ, không gộp ép.
    [Fact]
    public async Task BeforeApproval_KeepsTheInterviewPrompt()
    {
        var llm = new FakeLlm();
        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "Mình kể tiếp nhé");

        var texts = llm.LastMessages.Select(m => m.Text ?? string.Empty).ToList();
        Assert.Equal("## prompt BusinessAnalyst/requirement-chat.v4.md", texts[0]);
        Assert.Contains(texts, t => t.Contains(PreMarkMarker + "-0", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains(OldSourceText, StringComparison.Ordinal));
        Assert.Equal(0, llm.SummaryCalls);
    }

    [Fact]
    public void ModeNeedsAnApprovedBrief_TheMark_AndNoTableTurn()
    {
        var brief = new ProjectDocumentLookup.ApprovedDocument("V1", "x");

        Assert.True(RequirementChangeMode.IsActive(brief, 6, InterviewTableKind.None));
        Assert.False(RequirementChangeMode.IsActive(null, 6, InterviewTableKind.None));
        Assert.False(RequirementChangeMode.IsActive(brief, 0, InterviewTableKind.None));
        Assert.False(RequirementChangeMode.IsActive(brief, 6, InterviewTableKind.FlowMap));
    }

    private async Task ApproveAsync()
    {
        await using var db = NewDb();
        db.ProjectDocuments.Add(new ProjectDocument
        {
            ProjectId = _projectId,
            FileName = "ProductBrief.docx",
            VersionName = "V1",
            IsApproved = true,
            Content = BriefText,
            CreatedAt = _t0.AddMinutes(10),
        });
        var project = await db.Projects.FirstAsync(p => p.Id == _projectId);
        project.BriefApprovedTurnCount = 6;
        await db.SaveChangesAsync();
    }

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
            new ReportMapDraftService(db, llm, prompts),
            new UxDesignService(db, llm, prompts),
            new PermissionMatrixDraftService(db, llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Chỉ lời gọi tóm tắt hội thoại thành công (để thấy phần trước mốc được gộp); mọi lời gọi phụ khác
    // fail-open nên để hỏng hết.
    private sealed class FakeLlm : ILlmClient
    {
        public List<ChatMessage> LastMessages = new();
        public bool SummaryFails;
        public int SummaryCalls;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            if (logContext.Purpose != "BAConversationSummary")
                return Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

            SummaryCalls++;
            return Task.FromResult(SummaryFails
                ? new LlmCallResult { IsSuccess = false, ErrorMessage = "boom" }
                : new LlmCallResult { IsSuccess = true, Content = "Tóm tắt buổi phỏng vấn." });
        }

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            if (logContext.Purpose != "BAChat")
                return Task.FromResult((new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" }, (T?)null));

            LastMessages = messages.ToList();
            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" },
                (T?)(object)new BAChatReply { Message = "Tòa nhà gồm nhiều khu hay ngược lại?" }));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt " + relativePath;
    }
}
