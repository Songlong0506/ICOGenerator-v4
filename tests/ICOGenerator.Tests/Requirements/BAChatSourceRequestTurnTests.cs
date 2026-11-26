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

// XIN FILE NGAY TẠI LƯỢT NGƯỜI DÙNG NHẮC TỚI NÓ.
//
// Luật này nằm trong requirement-chat.v4.md từ lâu, in đậm, kèm một ca thật — và nó vẫn trượt, trượt im
// lặng: không cổng nào biết rằng buổi phỏng vấn vừa bỏ qua một file.
//
// Ca thật (dự án JD Libary 5, lượt 3 và 5): người dùng kể "1 file excel danh sách JD được dùng trong nhà
// máy… và 1 file excel khác để quản lý JD được gán cho nhân viên" — nhắc TỚI HAI LẦN — và không lượt nào
// trong cả 26 lượt xin file. Hậu quả không dừng ở một tệp đính kèm thiếu: không có file thì không có BẢNG
// CỘT để người dùng chốt phạm vi cột, nên toàn bộ mô hình dữ liệu của dự án được dựng từ trí nhớ họ gõ
// tay trong một lượt chat — đúng thứ đang nằm sẵn trong file.
//
// Lượt xin file THAY TRỌN lượt: xin file là lời nhờ HÀNH ĐỘNG, người dùng đọc xong đi tìm file nên mọi
// câu hỏi kèm theo bị nuốt mất, trong khi bản đồ bao phủ vẫn tính là đã hỏi.
//
// "Lượt user có nhắc tới một file đang dùng không" là phán quyết của lượt trọng tài (InterviewJudge), không
// còn là bảng cụm "excel / bảng tính / file…": bảng đó bắt cả chữ "file" trong "cho phép upload file đính
// kèm" (một tính năng, không phải một nguồn dữ liệu) và phải kê lại cho từng ngôn ngữ. Các test dưới đây
// ghim phần TẤT ĐỊNH quanh phán quyết đó: bắn một lần, không bắn khi đã có lời xin, đóng dấu lên lượt lưu.
public class BAChatSourceRequestTurnTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    private static readonly string Map =
        CoverageMapFixture.Map("- ★ Mục tiêu / bài toán: [RÕ] Quản lý JD và gán cho nhân viên.\n"
        + "- Quy trình hiện tại & điểm khó: [MỘT PHẦN] Đang dùng Excel. còn thiếu: chỗ nào mất thời gian nhất");

    private const string ModelQuestion = "Trong cách làm hiện nay, chỗ nào làm anh/chị mất thời gian nhất?";

    // Trọng tài phán: lượt người dùng vừa rồi CÓ nhắc tới một file đang dùng.
    private static StubInterviewJudge MentionsSource() =>
        new() { Turn = new TurnJudgeVerdict { UserMentionsExistingSource = true } };

    public BAChatSourceRequestTurnTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "app quản lý JD" });
        db.SaveChanges();
    }

    [Fact]
    public async Task MentioningAnExcelFileTakesOverTheTurnWithARequestForIt()
    {
        var llm = new FakeLlm(Map)
        {
            ChatReply = new BAChatReply
            {
                Message = ModelQuestion,
                Suggestions = new List<string> { "Phải sửa tay ở 2 file", "Khó biết JD nào gán cho ai" }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm, MentionsSource()).ChatAsync(
            _projectId,
            "hiện tại việc tạo và gán JD được HRBP thực hiện trong file excel, có 1 file excel danh sách JD");

        Assert.Equal(SourceRequestTurn.Message, result.Reply);

        // Chỗ trả lời là ô nhập: họ đính kèm, hoặc nhắn lại là không có file. Chip bấm-là-gửi ở đây sẽ
        // cuốn mất lượt trước khi họ kịp đi tìm file.
        Assert.True(result.OpenEnded);
        Assert.Empty(result.Suggestions);

        // Bản LƯU phải là bản người dùng thấy — chính lượt này là thứ chốt chặn đọc lại để không xin lần hai.
        var saved = await LastAssistantTurnAsync();
        Assert.Equal(result.Reply, saved.Message);

        // ... và nó phải mang CỜ. Phanh chống-giục đọc cột này, nên một lượt xin file không được đóng dấu
        // là một lượt vô hình với chính cái phanh ấy.
        Assert.True(saved.SourceRequested);
    }

    // Phanh chống-giục đọc CỜ, không đọc chữ. Trước đây nó quét cụm "📎"/"đính kèm"/"gửi giúp" trên mọi
    // lượt BA trong cửa sổ hội thoại — tức app đoán lại chính chuỗi mà nó tự phát ra — nên một lượt BA
    // bình thường có chữ "đính kèm" là khoá vĩnh viễn đường xin file, im lặng và không lấy lại được.
    [Fact]
    public async Task AnOrdinaryTurnThatMerelyMentionsAttachmentsDoesNotBlockTheRequest()
    {
        await using (var seed = NewDb())
        {
            seed.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId,
                AgentId = _baId,
                Role = "assistant",
                // Một câu hỏi khai thác bình thường, chỉ tình cờ mang đúng hai chữ mà bảng cụm từ cũ dò.
                Message = "Khi HRBP gửi giúp một JD mới, hồ sơ đính kèm gồm những gì ạ?",
                CreatedAt = DateTime.UtcNow.AddMinutes(-2)
            });
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm(Map) { ChatReply = new BAChatReply { Message = ModelQuestion } };

        await using var db = NewDb();
        var result = await NewSut(db, llm, MentionsSource()).ChatAsync(_projectId, "danh sách JD bên em đang để trong file excel");

        Assert.Equal(SourceRequestTurn.Message, result.Reply);
    }

    // Chỉ bắn MỘT lần: giục lần hai là phí đúng cái lượt mà luật này sinh ra để tiết kiệm. Người dùng nói
    // không có file thì hội thoại đi tiếp bình thường.
    [Fact]
    public async Task TheRequestIsNotRepeatedOnceItHasBeenAsked()
    {
        await using (var seed = NewDb())
        {
            seed.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId,
                AgentId = _baId,
                Role = "user",
                Message = "danh sách JD bên em đang để trong file excel",
                // Đúng thứ đường ghi thật đóng dấu lên lượt user khi trọng tài phán "có nhắc nguồn".
                MentionsSource = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-3)
            });
            seed.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId,
                AgentId = _baId,
                Role = "assistant",
                Message = SourceRequestTurn.Message,
                // Đúng thứ đường ghi thật đóng dấu lên lượt xin file (BAChatService) — phanh chống-giục
                // đọc CẶP cờ (lượt user nhắc nguồn → lượt BA xin file), không dò lại chữ của lượt cũ.
                SourceRequested = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-2)
            });
            seed.AgentConversations.Add(new AgentConversation
            {
                ProjectId = _projectId,
                AgentId = _baId,
                Role = "user",
                Message = "file đó bên em không gửi ra ngoài được",
                CreatedAt = DateTime.UtcNow.AddMinutes(-1)
            });
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm(Map) { ChatReply = new BAChatReply { Message = ModelQuestion } };

        await using var db = NewDb();
        // Kể cả khi trọng tài vẫn phán "có nhắc tới file": đã xin một lần thì không xin nữa.
        var result = await NewSut(db, llm, MentionsSource()).ChatAsync(_projectId, "vẫn là file excel đó, HRBP tự sửa");

        Assert.Equal(ModelQuestion, result.Reply);
    }

    // LỜI MỜI CHUNG CHUNG KHÔNG ĐỐT SUẤT. Prompt bắt lượt mở đầu mời kể một mạch VÀ đính kèm tài liệu nếu
    // có, nên lượt mở đầu nào cũng có nút 📎 và mang cờ SourceRequested. Ca thật (dự án Learning &
    // Development): người dùng đáp "File Excel, Email" hai lượt sau lời mời đó, và đường xin file tất định
    // không bắn vì "đã xin rồi". Lời mời trước khi người dùng nói có file không phải lời xin file đó.
    [Fact]
    public async Task AGenericInvitationInTheOpener_DoesNotUseUpTheRequest()
    {
        await SeedOpenerAndToolQuestionAsync();

        var llm = new FakeLlm(Map) { ChatReply = new BAChatReply { Message = ModelQuestion } };

        await using var db = NewDb();
        var result = await NewSut(db, llm, MentionsSource()).ChatAsync(_projectId, "File Excel, Email");

        Assert.Equal(SourceRequestTurn.Message, result.Reply);

        // Lượt user mang dấu "đã nhắc nguồn" — nửa còn lại của cặp mà suất xin file đọc ở mọi lượt sau.
        await using var check = NewDb();
        var userTurn = await check.AgentConversations
            .Where(c => c.ProjectId == _projectId && c.Role == "user")
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .FirstAsync();
        Assert.True(userTurn.MentionsSource);
        Assert.True(SourceRequestTurn.AnsweredMention(
            await check.AgentConversations.Where(c => c.ProjectId == _projectId).OrderBy(c => c.CreatedAt).ToListAsync()));
    }

    // ĐÚNG CA TRONG ẢNH CHỤP: người dùng vừa nói dữ liệu nằm trong file Excel, model tự xin đúng file đó, và
    // trọng tài (đọc sai) coi lời xin là hỏi lại câu "đang dùng công cụ gì". Lời xin đến hạn không xin lại
    // thông tin nào — nó xin chính cái file — nên phanh chống hỏi lại không được thay nó bằng câu của nhóm
    // khác.
    [Fact]
    public async Task ADueFileRequestTheModelWrites_IsNotTakenForARepeat()
    {
        await SeedOpenerAndToolQuestionAsync();

        const string modelRequest = "Anh/chị đang dùng file Excel để lên kế hoạch mở lớp. Anh/chị đính kèm giúp mình "
            + "file đó bằng nút 📎 ngay dưới ô nhập nhé.";
        var judge = new StubInterviewJudge
        {
            Turn = new TurnJudgeVerdict
            {
                RepeatsJudged = true, Repeats = { 1 }, UserMentionsExistingSource = true, AsksForFile = true
            }
        };
        var llm = new FakeLlm(Map) { ChatReply = new BAChatReply { Message = modelRequest, OpenEnded = true } };

        await using var db = NewDb();
        var result = await NewSut(db, llm, judge).ChatAsync(_projectId, "File Excel, Email");

        Assert.Equal(modelRequest, result.Reply);
        Assert.True((await LastAssistantTurnAsync()).SourceRequested);
    }

    // Không phụ thuộc ngôn ngữ: phần tất định không đọc một chữ nào của lượt user, nên một buổi phỏng vấn
    // tiếng Anh đi đúng con đường đó.
    [Fact]
    public async Task AnEnglishMentionOfASpreadsheetAlsoTakesOverTheTurn()
    {
        var judge = MentionsSource();
        var llm = new FakeLlm(Map) { ChatReply = new BAChatReply { Message = "Which step takes the most time today?" } };

        await using var db = NewDb();
        var result = await NewSut(db, llm, judge).ChatAsync(_projectId, "we currently track all job descriptions in a spreadsheet");

        Assert.Equal("we currently track all job descriptions in a spreadsheet", judge.LastUserMessage);
        Assert.Equal(SourceRequestTurn.Message, result.Reply);
    }

    // Model TỰ viết lời xin file (không có nút 📎 của câu dựng sẵn, không có dấu hỏi): trọng tài phán
    // `asksForFile`, nên lượt đó (1) không bị thay bằng câu dựng sẵn — xin hai lần trong một lượt là thừa,
    // (2) không bị coi là LƯỢT CÂM dù không có dấu hỏi, và (3) được đóng dấu để không ai xin lần nữa.
    [Fact]
    public async Task AFileRequestWrittenByTheModelItself_IsKept_AndStamped()
    {
        const string modelRequest = "Anh/chị gửi file danh sách JD đang dùng lên đây để mình đọc trước nhé.";
        var judge = new StubInterviewJudge
        {
            Turn = new TurnJudgeVerdict { UserMentionsExistingSource = true, AsksForFile = true }
        };
        var llm = new FakeLlm(Map) { ChatReply = new BAChatReply { Message = modelRequest, OpenEnded = true } };

        await using var db = NewDb();
        var result = await NewSut(db, llm, judge).ChatAsync(_projectId, "danh sách JD bên em đang để trong file excel");

        Assert.Equal(modelRequest, result.Reply);
        Assert.True((await LastAssistantTurnAsync()).SourceRequested);
    }

    // Trọng tài lỗi / không có ⇒ không đoán: câu hỏi của BA giữ nguyên. Fail-open — mất một cơ hội xin
    // file, đúng hành vi trước khi có chốt chặn này, còn hơn đốt một lượt của người dùng vì một phép đoán.
    [Fact]
    public async Task WithoutAVerdict_TheTurnIsLeftAlone()
    {
        var llm = new FakeLlm(Map) { ChatReply = new BAChatReply { Message = ModelQuestion } };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "danh sách JD bên em đang để trong file excel");

        Assert.Equal(ModelQuestion, result.Reply);
    }

    // Ranh giới: lượt user không nhắc tới vật mang dữ liệu nào thì câu hỏi của BA được giữ nguyên. Chốt
    // chặn này chỉ đổi lượt khi có một file thật để xin.
    [Fact]
    public async Task ATurnWithoutASourceMentionIsLeftAlone()
    {
        var llm = new FakeLlm(Map) { ChatReply = new BAChatReply { Message = ModelQuestion } };

        await using var db = NewDb();
        var result = await NewSut(db, llm, new StubInterviewJudge()).ChatAsync(_projectId, "manager sẽ là người tạo JD cho orgUnit của mình");

        Assert.Equal(ModelQuestion, result.Reply);
    }

    // Hội thoại của ca thật: lượt mở đầu mời kể + đính kèm nếu có (nút 📎 ⇒ cờ SourceRequested), người dùng
    // kể quy trình (không nhắc file), BA hỏi đang dùng công cụ gì.
    private async Task SeedOpenerAndToolQuestionAsync()
    {
        await using var seed = NewDb();
        var t = DateTime.UtcNow.AddMinutes(-10);
        seed.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId, AgentId = _baId, Role = "user", Message = "Cần app lên kế hoạch đào tạo", CreatedAt = t
        });
        seed.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId, AgentId = _baId, Role = "assistant",
            Message = "Anh/chị kể giúp mình một mạch nhé: hiện nay lên kế hoạch mở lớp làm những bước nào? Nếu có "
                + "file Excel hay biểu mẫu đang dùng, anh/chị đính kèm bằng nút 📎 ngay dưới ô nhập giúp mình.",
            SourceRequested = true,
            CreatedAt = t.AddSeconds(1)
        });
        seed.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId, AgentId = _baId, Role = "user",
            Message = "Đầu năm HR xem danh sách nhân viên, chọn khóa học, lên kế hoạch rồi trình HoD HR duyệt",
            CreatedAt = t.AddSeconds(2)
        });
        seed.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId, AgentId = _baId, Role = "assistant",
            Message = "Hiện nay, HR đang dùng công cụ gì để lên kế hoạch mở lớp?", CreatedAt = t.AddSeconds(3)
        });
        await seed.SaveChangesAsync();
    }

    private async Task<AgentConversation> LastAssistantTurnAsync()
    {
        await using var db = NewDb();
        return await db.AgentConversations
            .Where(c => c.ProjectId == _projectId && c.Role == "assistant")
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .FirstAsync();
    }

    // Cùng harness dựng BAChatService như BAChatSilentTurnTests.
    private static BAChatService NewSut(AppDbContext db, ILlmClient llm, InterviewJudge? judge = null)
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
            new ReportMapDraftService(db, llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null,
            judge: judge);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeLlm : ILlmClient
    {
        private readonly string? _coverageMap;

        public FakeLlm(string? coverageMap) => _coverageMap = coverageMap;

        public BAChatReply ChatReply = new() { Message = "Đã ghi nhận." };

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            if (logContext.Purpose != "BARequirementCoverage")
                return Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

            return Task.FromResult(_coverageMap == null
                ? new LlmCallResult { IsSuccess = false, ErrorMessage = "distill lỗi" }
                : new LlmCallResult { IsSuccess = true, Content = _coverageMap });
        }

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {

            // Bản đồ bao phủ nay đi qua đường structured output (RequirementCoverageService). Fake trả về
            // Value null để service rơi xuống nhánh parse văn xuôi — đúng nhánh mà một model không nhận
            // response_format sẽ chạy — nên các test ở đây vẫn seed bản đồ bằng text như trước.
            if (logContext.Purpose == "BARequirementCoverage")
                return Task.FromResult((ChatWithLogAsync(model, messages, logContext, onToken, cancellationToken).Result, (T?)null));
            if (logContext.Purpose != "BAChat")
                throw new InvalidOperationException($"Unexpected structured call: {logContext.Purpose}");

            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)(object)ChatReply));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }
}
