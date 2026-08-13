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
//
// Và khóa luôn THỨ TỰ của hai việc: bảng cột trước, bản đọc lại sau. Trước đây lượt upload vừa bày bảng
// vừa kể lại cả file kèm cụm "Chỗ chưa chắc" — tức là dựng việc tồn trên những cột người dùng sắp bỏ tích
// ngay bên dưới, và đọc nhầm cả file khi họ gửi nhầm file. Nay lượt upload chỉ giới thiệu ngắn + bảng, còn
// bản đọc lại dời sang đúng lượt chat kế tiếp (tin nhắn chốt bảng do server soạn nên nhận ra được chắc
// chắn). Cả hai đầu của cơ chế đó đều phải có chốt chặn, vì hỏng đầu nào cũng im lặng.
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

    // HÌNH DẠNG của lượt đọc file do CƠ CHẾ chọn: còn bảng tính chưa chốt cột ⇒ lượt này chỉ bày bảng kèm
    // lời giới thiệu ngắn. Model nhìn thấy text của MỌI nguồn trong project nên nó không tự biết file nào
    // đang chờ — để nó đoán là quay lại đúng lượt cũ: một bản đọc lại 18 cột đặt ngay trên cái bảng chở
    // cùng nội dung ở dạng sửa được.
    [Fact]
    public async Task Acknowledge_WithAPendingSpreadsheet_OrdersTheModelToDeferTheReadback()
    {
        var llm = new FakeLlm
        {
            AckReply = Ack(new SourceColumnNote { FileName = "74a9af7d-KeHoach.xlsx", Column = "Global ID", Meaning = "mã nhân viên", Used = true })
        };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        var shape = Assert.Single(llm.LastAckSystemMessages, m => m.StartsWith("## LƯỢT NÀY:", StringComparison.Ordinal));
        Assert.StartsWith("## LƯỢT NÀY: CHỐT PHẠM VI CỘT", shape, StringComparison.Ordinal);
        // Gọi ĐÍCH DANH file đang chờ: lô upload lẫn cả Excel lẫn Word thì Word vẫn được đọc lại đầy đủ.
        Assert.Contains("74a9af7d-KeHoach.xlsx", shape, StringComparison.Ordinal);
        Assert.Contains("Chỗ chưa chắc", shape, StringComparison.Ordinal);
    }

    // Mọi bảng tính đã chốt cột ⇒ không còn bảng nào để tích, lượt đọc file quay về đúng hình dạng cũ (bản
    // đọc lại + hai chip). Chọn sai chiều này là lượt câm: giới thiệu ngắn rồi mời rà một cái bảng không có.
    [Fact]
    public async Task Acknowledge_WithNothingLeftToTick_AsksForTheFullReadback()
    {
        await using (var seed = NewDb())
        {
            var source = await seed.ProjectSourceFiles.FirstAsync(s => s.Id == _sourceId);
            source.ColumnMap = """[{"FileName":"74a9af7d-KeHoach.xlsx","Column":"Global ID","Meaning":"mã nhân viên","Used":true}]""";
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm { AckReply = Ack() };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        var shape = Assert.Single(llm.LastAckSystemMessages, m => m.StartsWith("## LƯỢT NÀY:", StringComparison.Ordinal));
        Assert.StartsWith("## LƯỢT NÀY: BẢN ĐỌC LẠI", shape, StringComparison.Ordinal);
    }

    // Model được lệnh "mời người dùng rà bảng bên dưới" nhưng rốt cuộc không trả nổi dòng `columns` nào
    // dùng được ⇒ câu mời đó trỏ vào một cái bảng KHÔNG tồn tại, và người dùng đi tìm một cái nút không có
    // trên màn hình. Nói thẳng ra và mở đường khác, thay vì để họ tự đoán.
    [Fact]
    public async Task Acknowledge_WhenTheTableCouldNotBeBuilt_SaysSoInsteadOfPointingAtNothing()
    {
        var llm = new FakeLlm { AckReply = Ack() };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        await using var verify = NewDb();
        var turn = await verify.AgentConversations.Where(c => c.Role == "assistant").OrderBy(c => c.CreatedAt).LastAsync();
        Assert.Null(turn.ColumnMap);
        Assert.Contains(BAChatService.ColumnMapMissingNotice.Trim(), turn.Message, StringComparison.Ordinal);
    }

    // NỬA SAU của cơ chế: người dùng gửi bảng đi ⇒ lượt chat kế tiếp là lượt BA KỂ LẠI cách hiểu file theo
    // đúng bộ cột vừa chốt. Nhận diện bằng tin nhắn do SERVER soạn, không phải bằng một cờ client gửi lên.
    // Mất lượt này thì bản đọc lại không bao giờ diễn ra: cái sai duy nhất còn lại ở đầu vào (BA hiểu file
    // kể chuyện gì) chảy thẳng vào Product Brief mà người dùng không có chỗ nào để bác.
    [Fact]
    public async Task Chat_RightAfterTheColumnTableIsConfirmed_IsAReadbackTurn()
    {
        var confirmed = await ConfirmColumnMapAsync();

        var llm = new FakeLlm { ChatReply = ReplyWithQuestions() };
        await using (var db = NewDb())
            Assert.Equal(ChatWithBAResult.Ok, (await NewSut(db, llm).ChatAsync(_projectId, confirmed)).Status);

        Assert.Contains(llm.LastChatSystemMessages, m => m.Contains("source-readback.v1.md", StringComparison.Ordinal));

        // …và lượt đó chỉ có MỘT chỗ trả lời: hai chip xác nhận. Một thẻ hỏi gộp ở đây nuốt mất chúng
        // (thẻ hỏi và chip loại trừ nhau trên màn hình), tức nuốt mất thứ duy nhất lượt này cần lấy.
        await using var verify = NewDb();
        var turn = await verify.AgentConversations.Where(c => c.Role == "assistant").OrderBy(c => c.CreatedAt).LastAsync();
        Assert.Null(turn.Questions);
        Assert.NotNull(turn.Suggestions);
    }

    [Fact]
    public async Task Chat_OnAnOrdinaryMessage_HasNoReadbackBlock()
    {
        await ConfirmColumnMapAsync();

        var llm = new FakeLlm();
        await using (var db = NewDb())
            await NewSut(db, llm).ChatAsync(_projectId, "Mỗi lớp có sĩ số tối thiểu 8 người.");

        Assert.DoesNotContain(llm.LastChatSystemMessages, m => m.Contains("source-readback.v1.md", StringComparison.Ordinal));
    }

    // Chốt bảng qua đúng use case thật để test không tự dựng một tin nhắn "gần giống" — chính câu mở đầu
    // của tin nhắn ĐÓ là thứ mở cổng lượt kể lại.
    private async Task<string> ConfirmColumnMapAsync()
    {
        await using var db = NewDb();
        var result = await new ICOGenerator.Application.Requirements.ConfirmSourceColumnMapUseCase(db).ExecuteAsync(
            _projectId,
            """
            [{"fileName":"74a9af7d-KeHoach.xlsx","column":"Global ID","meaning":"mã số nhân viên","used":true},
             {"fileName":"74a9af7d-KeHoach.xlsx","column":"Revision Number","meaning":"phiên bản nội dung","used":false}]
            """);

        Assert.Equal(1, result.Files);
        return result.Message;
    }

    private static BAChatReply ReplyWithQuestions() => new()
    {
        Message = "Mình đọc file theo các cột anh/chị vừa chốt: …\nMình hiểu vậy đã đúng chưa ạ?",
        Suggestions = { "Đúng rồi", "Có chỗ chưa đúng" },
        Questions =
        {
            new BAChatQuestion { Question = "Mỗi lớp tối đa bao nhiêu người?" },
            new BAChatQuestion { Question = "Ai duyệt kế hoạch từng quý?" }
        }
    };

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

    // Các lời gọi phụ trợ (bộ nhớ, hồ sơ user, bản đồ bao phủ, nhật ký) đều fail-open nên để hỏng hết;
    // test này chỉ quan tâm hai lượt có thật: lượt đọc file và lượt chat ngay sau khi chốt bảng.
    private sealed class FakeLlm : ILlmClient
    {
        public BASourceAckReply AckReply = new() { Message = "Đã đọc." };
        public BAChatReply ChatReply = new() { Message = "Đã ghi nhận." };
        public List<string> LastAckSystemMessages = new();
        public List<string> LastChatSystemMessages = new();

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            var systemMessages = messages
                .Where(m => m.Role == ChatRole.System)
                .Select(m => m.Text ?? string.Empty)
                .ToList();

            object? value;
            switch (logContext.Purpose)
            {
                case "BASourceAck":
                    LastAckSystemMessages = systemMessages;
                    value = AckReply;
                    break;
                case "BAChat":
                    LastChatSystemMessages = systemMessages;
                    value = ChatReply;
                    break;
                default:
                    return Task.FromResult((new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" }, (T?)null));
            }

            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)value));
        }
    }

    // Nhả lại ĐƯỜNG DẪN prompt thay vì một chuỗi cố định: đó là cách duy nhất để test thấy được lượt nào
    // được đính thêm khối source-readback mà không phải chép nội dung prompt thật vào test.
    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub: " + relativePath;
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
