using ICOGenerator.Contracts.Requirements;
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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// BẢNG CỘT được dựng ở LƯỢT ĐỌC FILE, và chỉ ở đó: đấy là lượt duy nhất model cầm trên tay khối "Thống kê
// cột" của cả bảng (mọi giá trị phân biệt kèm số dòng) — thứ cần để đoán nghĩa từng cột. Test này khóa cái
// vòng đó lại: đề xuất của model biến thành bảng phủ ĐỦ cột thật của file, dòng bịa bị loại, và lượt đó
// không đồng thời bày ra hai chỗ trả lời.
public class BASourceColumnMapTests : IDisposable
{
    private const string ExtractedText = """
        ### Sheet: Sheet1
        Tổng: 262 dòng dữ liệu, 4 cột.

        #### Thống kê cột (trên 262 dòng)
        - Global ID: có giá trị 262/262 · 13 giá trị phân biệt
        - Item Title: có giá trị 257/262 · 136 giá trị phân biệt
        - Assignment Type: có giá trị 136/262 · ĐỦ 3 giá trị: REQ (78), MAN (53), OPT (5)
        - Revision Number: có giá trị 262/262 · ĐỦ 3 giá trị: 1 (218), 3 (21), 2 (18)

        #### Dòng dữ liệu (1 dòng đầu làm mẫu)
        Global ID | Item Title | Assignment Type | Revision Number
        11054396 | Quality at Bosch-B | REQ | 1
        """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();
    private readonly Guid _sourceId = Guid.NewGuid();

    public BASourceColumnMapTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "kế hoạch lớp học" });
        db.ProjectSourceFiles.Add(NewSpreadsheet(_sourceId, _projectId, "74a9af7d-KeHoach.xlsx"));
        db.SaveChanges();
    }

    [Fact]
    public async Task Acknowledge_TurnsTheModelProposalIntoATableCoveringEveryRealColumn()
    {
        var llm = new FakeLlm
        {
            AckReply = Ack(
                new SourceColumnNote { FileName = "74a9af7d-KeHoach.xlsx", Column = "Global ID", Meaning = "mã số nhân viên", Used = true },
                new SourceColumnNote { FileName = "74a9af7d-KeHoach.xlsx", Column = "Revision Number", Meaning = "phiên bản nội dung", Used = false },
                // Cột model BỊA ra: người dùng tích vào nó thì bộ lọc dữ liệu mẫu đi tìm một cột không tồn tại.
                new SourceColumnNote { FileName = "74a9af7d-KeHoach.xlsx", Column = "Sức chứa phòng", Meaning = "bịa", Used = true })
        };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        var rows = await LoadTurnColumnMapAsync();
        Assert.Equal(new[] { "Global ID", "Item Title", "Assignment Type", "Revision Number" }, rows.Select(r => r.Column));
        Assert.DoesNotContain(rows, r => r.Column == "Sức chứa phòng");
        Assert.True(rows.Single(r => r.Column == "Global ID").Used);
        // Cột model không nhắc tới vẫn có mặt để người dùng tự quyết, thay vì biến mất trong im lặng.
        Assert.False(rows.Single(r => r.Column == "Item Title").Used);
        Assert.Equal("", rows.Single(r => r.Column == "Item Title").Meaning);
    }

    // Model gần như luôn chép cái tên NGƯỜI DÙNG gọi ("KeHoach.xlsx") chứ không phải tên đã lưu (upload gắn
    // tiền tố chống trùng). Chỉ có MỘT bảng tính đang chờ thì đích đến không thể là gì khác — vứt cả bảng
    // vì một chi tiết hình thức là mất luôn đường chốt phạm vi cột của file.
    [Fact]
    public async Task Acknowledge_StillBuildsTheTable_WhenTheModelUsedADifferentFileName()
    {
        var llm = new FakeLlm
        {
            AckReply = Ack(new SourceColumnNote { FileName = "KeHoach.xlsx", Column = "Global ID", Meaning = "mã nhân viên", Used = true })
        };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        var rows = await LoadTurnColumnMapAsync();
        Assert.Equal(4, rows.Count);
        // Bảng luôn mang tên file ĐÃ LƯU — đó là khóa mà lượt gửi lên dùng để ghép về đúng nguồn.
        Assert.All(rows, r => Assert.Equal("74a9af7d-KeHoach.xlsx", r.FileName));
    }

    // Lượt có bảng thì bảng LÀ chỗ trả lời. Để chip "Đúng rồi / Chưa đúng" sống cùng thì một cú bấm nhầm
    // gửi mất lượt trước khi người dùng kịp tích xong, và bảng không bao giờ được chốt.
    [Fact]
    public async Task Acknowledge_WithATable_DropsTheConfirmationChipsOfThatTurn()
    {
        var llm = new FakeLlm
        {
            AckReply = Ack(new SourceColumnNote { FileName = "74a9af7d-KeHoach.xlsx", Column = "Global ID", Meaning = "mã nhân viên", Used = true })
        };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        await using var verify = NewDb();
        var turn = await verify.AgentConversations.Where(c => c.Role == "assistant").OrderBy(c => c.CreatedAt).LastAsync();
        Assert.Null(turn.Suggestions);
        Assert.False(turn.SuggestionsMultiSelect);
    }

    // Không đề xuất nào dùng được ⇒ KHÔNG bày bảng, và lượt đọc file chạy y như trước (vẫn có chip xác
    // nhận). Một bảng toàn ô trống là lượt hỏng, không phải "để người dùng tự điền".
    [Fact]
    public async Task Acknowledge_WithoutUsableColumns_ShowsNoTable_AndKeepsTheNormalChips()
    {
        var llm = new FakeLlm { AckReply = Ack() };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        await using var verify = NewDb();
        var turn = await verify.AgentConversations.Where(c => c.Role == "assistant").OrderBy(c => c.CreatedAt).LastAsync();
        Assert.Null(turn.ColumnMap);
        Assert.NotNull(turn.Suggestions);
    }

    // Lượt đọc file nạp lại TOÀN BỘ nguồn của project. Không loại các file đã chốt thì mỗi lần đính thêm
    // một file mới, các bảng đã tích xong hiện lại y nguyên để tích lần nữa.
    [Fact]
    public async Task Acknowledge_DoesNotReopenATableTheUserAlreadyConfirmed()
    {
        await using (var seed = NewDb())
        {
            var source = await seed.ProjectSourceFiles.FirstAsync(s => s.Id == _sourceId);
            source.ColumnMap = """[{"FileName":"74a9af7d-KeHoach.xlsx","Column":"Global ID","Meaning":"mã nhân viên","Used":true}]""";
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm
        {
            AckReply = Ack(new SourceColumnNote { FileName = "74a9af7d-KeHoach.xlsx", Column = "Global ID", Meaning = "mã nhân viên", Used = true })
        };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        await using var verify = NewDb();
        var turn = await verify.AgentConversations.Where(c => c.Role == "assistant").OrderBy(c => c.CreatedAt).LastAsync();
        Assert.Null(turn.ColumnMap);
    }

    private static BASourceAckReply Ack(params SourceColumnNote[] columns) => new()
    {
        Message = "Mình đọc được file kế hoạch đào tạo. Mình hiểu vậy đúng chưa ạ?",
        Suggestions = { "Đúng rồi", "Có chỗ chưa đúng" },
        Columns = columns.ToList()
    };

    private static ProjectSourceFile NewSpreadsheet(Guid id, Guid projectId, string fileName) => new()
    {
        Id = id,
        ProjectId = projectId,
        FileName = fileName,
        Kind = SourceFileKind.Spreadsheet,
        ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        StoredPath = Path.Combine(Path.GetTempPath(), fileName),
        ExtractedText = ExtractedText
    };

    private async Task<List<SourceColumnNote>> LoadTurnColumnMapAsync()
    {
        await using var db = NewDb();
        var turn = await db.AgentConversations.Where(c => c.Role == "assistant").OrderBy(c => c.CreatedAt).LastAsync();
        return ConversationTurnRenderer.ParseColumnMap(turn.ColumnMap);
    }

    private BAChatService NewSut(AppDbContext db, ILlmClient llm)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var prompts = new StubPrompts();
        return new BAChatService(
            db,
            llm,
            prompts,
            new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance),
            new BAChatReplyParser(),
            new ConversationMemoryService(db, llm, prompts),
            new UserMemoryService(db, llm, prompts),
            new RequirementCoverageService(db, llm, prompts),
            new OrganizationContextService(db, prompts, new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new DecisionLogService(db, llm, prompts),
            new InterviewOutlookService(db, llm, prompts),
            new ChecklistNoteStore(db),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeLlm : ILlmClient
    {
        public BASourceAckReply AckReply = new() { Message = "Đã đọc." };

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            object value = logContext.Purpose switch
            {
                "BASourceAck" => AckReply,
                _ => throw new InvalidOperationException($"Unexpected structured call: {logContext.Purpose}")
            };
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
