using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Organization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ICOGenerator.Tests;

namespace ICOGenerator.Tests.Requirements;

// BẢNG CỘT được dựng ở LƯỢT ĐỌC FILE, và chỉ ở đó: đấy là lượt duy nhất model cầm trên tay khối "Thống kê
// cột" của cả bảng (mọi giá trị phân biệt kèm số dòng) — thứ cần để đoán nghĩa từng cột. Test này khóa cái
// vòng đó lại: đề xuất của model biến thành bảng phủ ĐỦ cột thật của file, dòng bịa bị loại, và bảng được
// LƯU NGAY vào file nguồn — người dùng không phải rà hay gửi gì.
//
// Lượt đọc file nay là MỘT lượt: BA nói ngay mình hiểu file ra sao rồi hỏi tiếp. Thiết kế cũ chia ba lượt
// (bày bảng cột bắt người dùng tích → người dùng gửi → lượt kể lại kèm "Chỗ chưa chắc" và "hiểu vậy đúng
// chưa?") — ba lượt trôi qua trước khi BA nói được điều gì người dùng chưa biết.
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
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = _model.Id });
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

    // Bảng cột không còn chờ người dùng gửi: phạm vi cột BA chốt được LƯU NGAY vào file nguồn — đó là thứ
    // RealSampleDataReader lọc dữ liệu mẫu theo và SourceContextBuilder đính vào mọi lượt sau. Và chip của
    // lượt là phương án trả lời CÂU HỎI KẾT của BA, nên chúng phải sống cùng bảng (bảng giờ là thứ thu gọn,
    // không phải chỗ trả lời của lượt).
    [Fact]
    public async Task Acknowledge_SavesTheColumnScopeRightAway_AndKeepsTheClosingQuestionChips()
    {
        var llm = new FakeLlm
        {
            AckReply = Ack(new SourceColumnNote { FileName = "74a9af7d-KeHoach.xlsx", Column = "Global ID", Meaning = "mã nhân viên", Used = true })
        };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        await using var verify = NewDb();
        var source = await verify.ProjectSourceFiles.SingleAsync(s => s.Id == _sourceId);
        Assert.Equal(new[] { "Global ID" }, SourceColumnMapBuilder.UsedColumns(source.ColumnMap));

        var turn = await verify.AgentConversations.Where(c => c.Role == "assistant").OrderBy(c => c.CreatedAt).LastAsync();
        Assert.NotNull(turn.ColumnMap);
        Assert.Equal(new[] { "Mỗi khu", "Theo người" }, ConversationTurnRenderer.ParseSuggestions(turn.Suggestions));
    }

    // Không đề xuất nào dùng được ⇒ KHÔNG lưu bảng nào, dữ liệu mẫu không bị lọc (đúng hành vi trước khi có
    // bảng cột), và bản đọc giữ nguyên — không có câu nào trỏ vào một cái bảng không tồn tại.
    [Fact]
    public async Task Acknowledge_WithoutUsableColumns_SavesNoTable_AndLeavesTheMessageAlone()
    {
        var llm = new FakeLlm { AckReply = Ack() };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        await using var verify = NewDb();
        Assert.Null((await verify.ProjectSourceFiles.SingleAsync(s => s.Id == _sourceId)).ColumnMap);
        var turn = await verify.AgentConversations.Where(c => c.Role == "assistant").OrderBy(c => c.CreatedAt).LastAsync();
        Assert.Null(turn.ColumnMap);
        Assert.Equal(Ack().Message, turn.Message);
        Assert.NotNull(turn.Suggestions);
    }

    // Lượt đọc file nạp lại TOÀN BỘ nguồn của project. Không loại các file đã có bảng thì mỗi lần đính thêm
    // một file mới, bảng của file trước — có thể người dùng đã sửa tay — bị một lời gọi model khác ghi đè.
    [Fact]
    public async Task Acknowledge_DoesNotRebuildOrOverwriteAnExistingTable()
    {
        const string existing = """[{"FileName":"74a9af7d-KeHoach.xlsx","Column":"Item Title","Meaning":"tên khóa học","Used":true}]""";
        await using (var seed = NewDb())
        {
            var source = await seed.ProjectSourceFiles.FirstAsync(s => s.Id == _sourceId);
            source.ColumnMap = existing;
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
        Assert.Equal(existing, (await verify.ProjectSourceFiles.SingleAsync(s => s.Id == _sourceId)).ColumnMap);
    }

    // File nào cần chốt bảng cột do CƠ CHẾ nói ra: model nhìn thấy text của MỌI nguồn trong project nên nó
    // không tự biết file nào còn chờ. Và khối đó phải nói rõ bảng được LƯU LUÔN — nếu không model quay lại
    // đúng hình dạng cũ: mời người dùng rà bảng rồi bấm gửi một cái nút không còn tồn tại.
    [Fact]
    public async Task Acknowledge_WithAPendingSpreadsheet_NamesItInTheColumnBlock()
    {
        var llm = new FakeLlm
        {
            AckReply = Ack(new SourceColumnNote { FileName = "74a9af7d-KeHoach.xlsx", Column = "Global ID", Meaning = "mã nhân viên", Used = true })
        };

        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        var block = ColumnBlock(llm);
        Assert.Contains("74a9af7d-KeHoach.xlsx", block, StringComparison.Ordinal);
        Assert.Contains("LƯU LUÔN", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acknowledge_WithNothingLeftToChoose_AsksForAnEmptyColumnList()
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

        Assert.Contains("`columns` là mảng RỖNG", ColumnBlock(llm), StringComparison.Ordinal);
    }

    // Lượt đọc file trước đây KHÔNG có một dòng hội thoại nào trong ngữ cảnh, trong khi chính prompt bắt nó
    // "đối chiếu với điều người dùng đã kể". Kết quả: bản đọc chỉ kể lại số đếm, còn phần đắt nhất — file là
    // BẰNG CHỨNG cho nỗi đau người dùng vừa nói — không có cách nào viết ra.
    [Fact]
    public async Task Acknowledge_GivesTheModelWhatTheUserHasSaidSoFar()
    {
        await using (var seed = NewDb())
        {
            seed.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId, AgentId = _baId, Role = "user",
                Message = "Data trong file excel nhiều khi không được update, nhân viên nghỉ việc phải dò tay.",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5)
            });
            seed.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId, AgentId = _baId, Role = "assistant",
                Message = "Anh/chị gửi giúp mình file đang dùng nhé.",
                CreatedAt = DateTime.UtcNow.AddMinutes(-4)
            });
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm { AckReply = Ack() };
        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        var context = Assert.Single(llm.LastAckSystemMessages, m => m.StartsWith("## BỐI CẢNH HỘI THOẠI", StringComparison.Ordinal));
        Assert.Contains("Người dùng: Data trong file excel nhiều khi không được update", context, StringComparison.Ordinal);
        Assert.Contains("BA: Anh/chị gửi giúp mình file đang dùng nhé.", context, StringComparison.Ordinal);
    }

    // Upload ngay lượt đầu (chưa có hội thoại nào) ⇒ không có khối rỗng ruột nào.
    [Fact]
    public async Task Acknowledge_OnAFreshProject_HasNoConversationBlock()
    {
        var llm = new FakeLlm { AckReply = Ack() };
        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        Assert.DoesNotContain(llm.LastAckSystemMessages, m => m.StartsWith("## BỐI CẢNH HỘI THOẠI", StringComparison.Ordinal));
    }

    // PHẠM VI KỂ LẠI. Lượt đọc file nạp lại TOÀN BỘ nguồn của project — cố ý, vì nguồn cũ là thứ duy nhất
    // để đối chiếu — nhưng "đính kèm để đối chiếu" khác hẳn "phải kể lại". Ca thật: người dùng gửi file
    // Excel ở đầu buổi, mười mấy lượt sau gửi một ảnh chụp biểu mẫu để trả lời một câu hỏi, và BA kể lại CẢ
    // HAI — mở đầu lượt bằng gần nửa số dòng nói lại đúng bộ cột của file Excel. Model không sai luật nào nó
    // được cho: nó thấy mọi nguồn nằm dưới cùng một câu "tôi vừa đính kèm". Chỗ hỏng là cơ chế nói dối về
    // chữ "vừa gửi", nên chốt chặn phải nằm ở cơ chế.
    [Fact]
    public async Task Acknowledge_ScopesTheReadbackToTheFilesJustSent()
    {
        var imageId = await AddConfirmedSpreadsheetAndNewImageAsync();

        var llm = new FakeLlm { AckReply = Ack() };
        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(
                _projectId, "đây là các field của biểu mẫu", new[] { new ChatAttachment(imageId, "bieu-mau.png", true) }));

        var scope = ScopeBlock(llm);
        Assert.Contains("VỪA GỬI ở lượt này: bieu-mau.png", scope, StringComparison.Ordinal);
        Assert.Contains("74a9af7d-KeHoach.xlsx", scope, StringComparison.Ordinal);
        Assert.Contains("TUYỆT ĐỐI KHÔNG kể lại chúng", scope, StringComparison.Ordinal);
        // Chỗ NỐI giữa file mới và nguồn cũ vẫn phải nói được — đó thường là điểm đắt nhất của cả lô upload
        // ("biểu mẫu này lấy người học từ file danh sách hay tự nhập?"). Cấm nhắc tên nguồn cũ là cắt luôn
        // điều đó, tức vá một lỗi bằng cách mở một lỗi nặng hơn.
        Assert.Contains("chỗ NỐI", scope, StringComparison.Ordinal);

        // Và câu của lượt user không được khai man rằng file cũ cũng vừa được gửi — đó là câu đứng ngay
        // trên text của mọi nguồn, nên nó quyết định model hiểu "vừa đính kèm" là những file nào.
        Assert.Contains("Tôi vừa đính kèm: bieu-mau.png", llm.LastAckUserText, StringComparison.Ordinal);
        Assert.DoesNotContain("Tôi vừa đính kèm: 74a9af7d-KeHoach.xlsx", llm.LastAckUserText, StringComparison.Ordinal);
    }

    // Nguồn cũ mà lượt này VẪN còn việc với nó không bị cấm nhắc tới: bảng tính cũ chưa có bảng cột được
    // chốt bảng ngay lượt này, nên câu phạm vi cột của lượt phải được gọi tên nó.
    [Fact]
    public async Task Acknowledge_DoesNotSilenceAnOlderSpreadsheetThatStillNeedsItsColumnTable()
    {
        var imageId = Guid.NewGuid();
        await using (var seed = NewDb())
        {
            seed.ProjectSourceFiles.Add(new ProjectSourceFile
            {
                Id = imageId,
                ProjectId = _projectId,
                FileName = "bieu-mau.png",
                Kind = SourceFileKind.Image,
                ContentType = "image/png",
                StoredPath = Path.Combine(Path.GetTempPath(), "bieu-mau.png"),
                IsVisionSource = true
            });
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm { AckReply = Ack() };
        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(
                _projectId, null, new[] { new ChatAttachment(imageId, "bieu-mau.png", true) }));

        Assert.Contains("74a9af7d-KeHoach.xlsx", ColumnBlock(llm), StringComparison.Ordinal);
        Assert.DoesNotContain("Các nguồn còn lại", ScopeBlock(llm), StringComparison.Ordinal);
    }

    // Không biết lô nào vừa gửi (caller không truyền danh sách file) ⇒ giữ nguyên hành vi cũ: kể lại tất.
    // Đoán bừa một tập con nguy hiểm hơn hẳn kể lại thừa — file vừa gửi mà rơi khỏi phạm vi thì bản đọc của
    // chính nó biến mất, im lặng.
    [Fact]
    public async Task Acknowledge_WithoutAnAttachmentList_KeepsReadingBackEverything()
    {
        await AddConfirmedSpreadsheetAndNewImageAsync();

        var llm = new FakeLlm { AckReply = Ack() };
        await using (var db = NewDb())
            Assert.True(await NewSut(db, llm).AcknowledgeSourcesAsync(_projectId));

        var scope = ScopeBlock(llm);
        Assert.Contains("VỪA GỬI ở lượt này: 74a9af7d-KeHoach.xlsx, bieu-mau.png", scope, StringComparison.Ordinal);
        Assert.DoesNotContain("TUYỆT ĐỐI KHÔNG kể lại chúng", scope, StringComparison.Ordinal);
    }

    private static string ColumnBlock(FakeLlm llm)
        => Assert.Single(llm.LastAckSystemMessages, m => m.StartsWith("## BẢNG CỘT CỦA LƯỢT NÀY", StringComparison.Ordinal));

    private static string ScopeBlock(FakeLlm llm)
        => Assert.Single(llm.LastAckSystemMessages, m => m.Contains("## PHẠM VI KỂ LẠI CỦA LƯỢT NÀY", StringComparison.Ordinal));

    private static BASourceAckReply Ack(params SourceColumnNote[] columns) => new()
    {
        Message = "File kế hoạch đào tạo có 262 dòng nhưng chỉ 13 nhân viên. Kế hoạch được lập theo khu hay theo từng người?",
        Suggestions = { "Mỗi khu", "Theo người" },
        Columns = columns.ToList()
    };

    // Dựng đúng thế trận của ca thật: một bảng tính đã có bảng cột từ đầu buổi, rồi người dùng gửi thêm
    // một ảnh ở lượt này. Trả về id của ảnh để test đóng vai lượt upload chỉ đính kèm đúng nó.
    private async Task<Guid> AddConfirmedSpreadsheetAndNewImageAsync()
    {
        var imageId = Guid.NewGuid();
        await using var seed = NewDb();

        var spreadsheet = await seed.ProjectSourceFiles.FirstAsync(s => s.Id == _sourceId);
        spreadsheet.ColumnMap = """[{"FileName":"74a9af7d-KeHoach.xlsx","Column":"Global ID","Meaning":"mã nhân viên","Used":true}]""";

        seed.ProjectSourceFiles.Add(new ProjectSourceFile
        {
            Id = imageId,
            ProjectId = _projectId,
            FileName = "bieu-mau.png",
            Kind = SourceFileKind.Image,
            ContentType = "image/png",
            StoredPath = Path.Combine(Path.GetTempPath(), "bieu-mau.png"),
            IsVisionSource = true,
            CreatedAt = spreadsheet.CreatedAt.AddMinutes(30)
        });

        await seed.SaveChangesAsync();
        return imageId;
    }

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
            new RequirementCoverageService(db, llm, prompts, new CoverageChecklist(prompts)),
            new OrganizationContextService(db, prompts,
                new OrgChartProvider(db, new MemoryCache(new MemoryCacheOptions())),
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new InterviewScopeService(db, llm, prompts),
            new ScreenStepPlacementService(llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Các lời gọi phụ trợ (bộ nhớ, hồ sơ user, bản đồ bao phủ, nhật ký) đều fail-open nên để hỏng hết;
    // test này chỉ quan tâm lượt có thật: lượt đọc file.
    private sealed class FakeLlm : ILlmClient
    {
        public BASourceAckReply AckReply = new() { Message = "Đã đọc." };
        public List<string> LastAckSystemMessages = new();
        public string LastAckUserText = string.Empty;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
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
                    // Chỉ khối text ĐẦU của lượt user — phần text các nguồn nối ngay sau nó, mà thứ đang
                    // xét là câu dẫn đứng trên chúng: chính câu đó nói cho model biết "vừa đính kèm" là
                    // những file nào.
                    LastAckUserText = messages.Last(m => m.Role == ChatRole.User).Contents
                        .OfType<TextContent>().First().Text;
                    value = AckReply;
                    break;
                default:
                    return Task.FromResult((new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" }, (T?)null));
            }

            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)value));
        }
    }

    // Nhả lại ĐƯỜNG DẪN prompt thay vì một chuỗi cố định: test không phải chép nội dung prompt thật.
    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub: " + relativePath;
    }
}
