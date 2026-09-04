using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Security;
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

// LƯỢT CÂM — lượt BA không hỏi gì cả, và vì thế không tầng nào phía trên nhìn thấy nó.
//
// Hai phanh tất định của lượt chat đều soi các lượt CÓ hỏi: AskedQuestionHistory so nội dung CÂU HỎI với
// các câu đã hỏi, còn cổng readiness chỉ vào cuộc khi lượt đó NHẮC TỚI nút "Write Requirement". Một lượt
// chỉ gồm câu ghi nhận rồi dừng lại lọt qua cả hai — không có câu hỏi để so, không có lời mời để chặn.
//
// Ca thật (dự án JD Libary 5, các lượt 82/84/90): một dòng bản đồ kẹt [MỘT PHẦN] dù người dùng đã trả lời
// đúng cái mẩu "còn thiếu:" của nó. BA hết đường hợp lệ — prompt cấm hỏi lại điều vừa được trả lời, và cấm
// nhắc tới nút khi bản đồ chưa đủ — nên nó viết "mình tiếp tục bước rà soát cuối", một bước không tồn tại.
// Người dùng đáp "ok", "tiếp tục đi", nhận lại đúng một lượt như thế, và buổi phỏng vấn 90 lượt kết thúc ở
// một lượt không ai trả lời được.
//
// Đây là ca mà bản đồ KHÔNG tự lành: nó chỉ nhúc nhích khi có thông tin mới, mà lượt câm thì không hỏi
// được gì để lấy thông tin mới. Vì vậy chốt chặn phải nằm ở lượt chat, không phải ở lượt distill sau đó.
public class BAChatSilentTurnTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    // Bản đồ đúng hình dạng đã đẻ ra bệnh: dòng ★ cốt lõi kẹt [MỘT PHẦN] với một mẩu "còn thiếu:" mà người
    // dùng đã trả lời trong hội thoại (nên BA không còn gì để hỏi), trong khi cổng thì vẫn đóng.
    private static readonly string PartialMap =
        CoverageMapFixture.DistillReply("- ★ Mục tiêu / bài toán: [RÕ] quản lý JD và assign cho nhân viên. {nguồn: \"quản lý danh sách JD\"}\n"
        + "- ★ Chức năng & luồng nghiệp vụ chính: [MỘT PHẦN] đã chốt luồng tạo–duyệt. còn thiếu: chức năng “Xác nhận đã ký đủ” nằm ở màn hình nào");

    private static readonly string ReadyMap =
        CoverageMapFixture.DistillReply("- ★ Mục tiêu / bài toán: [RÕ] quản lý JD và assign cho nhân viên. {nguồn: \"quản lý danh sách JD\"}\n"
        + "- ★ Chức năng & luồng nghiệp vụ chính: [RÕ] đã chốt luồng tạo–duyệt–assign. {nguồn: bảng luồng đã chốt}");

    public BAChatSilentTurnTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "app quản lý JD" });
        db.SaveChanges();
    }

    [Fact]
    public async Task ATurnThatAsksNothingIsReplacedByTheDeterministicFollowUp()
    {
        // Nguyên văn hình dạng đã gặp: câu ghi nhận + lời hứa về một "bước" không tồn tại, không chip,
        // không dấu hỏi. Model tin rằng nó đang tiến triển; người dùng thì không có gì để bấm hay trả lời.
        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply
            {
                Message = "Mình ghi nhận: chức năng “Xác nhận đã ký đủ” được giữ trên cả hai trang HRBP. "
                          + "Các nội dung nghiệp vụ còn lại đã được chốt; mình tiếp tục bước rà soát cuối."
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Đúng rồi, tiếp tục");

        // Lượt câm bị thay bằng đúng bước kế tiếp tất định: câu chặn của cổng, hỏi đúng mẩu "còn thiếu:"
        // — thứ người dùng trả lời được — và không đọc nhãn nhóm của bản đồ ra màn hình.
        Assert.DoesNotContain("tiếp tục bước rà soát cuối", result.Reply);
        Assert.DoesNotContain("Chức năng & luồng nghiệp vụ chính", result.Reply);
        Assert.Contains("nằm ở màn hình nào", result.Reply);
        Assert.EndsWith("?", result.Reply.TrimEnd());

        // Câu chặn không có chip ⇒ ô nhập phải nhận vai chỗ trả lời, nếu không màn hình lại có một câu hỏi
        // không có nút nào.
        Assert.True(result.OpenEnded);
        Assert.Empty(result.Suggestions);

        // Bản LƯU phải là bản người dùng thấy: cổng giữ sổ "đã hỏi câu nào" bằng cách đọc lại chính các
        // lượt đã lưu, nên lưu bản gốc là cổng mất sổ và phát lại đúng câu này ở lượt sau.
        var saved = await LastAssistantTurnAsync();
        Assert.Equal(result.Reply, saved.Message);
    }

    [Fact]
    public async Task ATurnThatStillAsksSomethingIsLeftAlone()
    {
        const string question = "Sau khi đủ chữ ký tay, HRBP xác nhận trên ứng dụng theo cách nào?";
        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply
            {
                Message = question,
                Suggestions = new List<string> { "Chỉ tích xác nhận", "Tải bản đã ký lên" }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "ok");

        Assert.Equal(question, result.Reply);
        Assert.Equal(2, result.Suggestions.Count);
    }

    // Ranh giới quan trọng nhất của chốt chặn này: một lượt CÓ hỏi mà quên chip vẫn trả lời được bằng ô
    // nhập (luôn mở), nên nó KHÔNG phải lượt câm. Thay nó đi là cướp mất câu hỏi thật của BA — thường là
    // loại đắt nhất, câu xin lời kể — để phát một câu chặn khô cứng hơn.
    [Fact]
    public async Task AQuestionWithoutChipsIsNotSilent()
    {
        const string question = "Trước đây các anh/chị quản lý JD bằng cách nào?";
        var llm = new FakeLlm(PartialMap) { ChatReply = new BAChatReply { Message = question } };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "ok");

        Assert.Equal(question, result.Reply);
    }

    // LỖ THỦNG ĐÃ CÓ THẬT của chốt chặn này: model đặt `openEnded: true` cho một lượt không hỏi gì, và cờ
    // đó mua được quyền miễn trừ. Nhưng cờ chỉ mở một Ô NHẬP — mà ô nhập thì lượt nào cũng có; thứ mời
    // người dùng gõ vào đó là CÂU HỎI.
    //
    // Ca thật (dự án JD Libary 5, lượt 18): "Để mình tổng hợp lại những gì đã chốt và hỏi thêm một số điểm
    // còn lại nhé." — đúng hình dạng lượt câm mà prompt cấm bằng tên ("KHÔNG kết bằng lời hứa về một bước
    // bạn sắp làm"), người dùng đáp "ok" ở lượt 19 và lượt đó mất trắng.
    [Fact]
    public async Task AnOpenEndedFlagDoesNotExemptATurnThatAsksNothing()
    {
        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply
            {
                Message = "Cảm ơn anh/chị! Mình đã ghi nhận: khi gán JD chỉ cần ngày hiệu lực. "
                          + "Để mình tổng hợp lại những gì đã chốt và hỏi thêm một số điểm còn lại nhé.",
                OpenEnded = true
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Không, chỉ cần ngày hiệu lực");

        Assert.DoesNotContain("tổng hợp lại những gì đã chốt", result.Reply);
        Assert.Contains("nằm ở màn hình nào", result.Reply);
        Assert.EndsWith("?", result.Reply.TrimEnd());
    }

    // NHỊP TÓM TẮT KIỂM CHỨNG mà quên chip: lượt này là câu ĐÓNG (gật, hoặc đòi sửa) nên nó phải có nút để
    // bấm. Không có hai nhánh bày sẵn thì model tự trượt sang hỏi độ ĐẦY ĐỦ của cả buổi phỏng vấn — ca
    // thật JD Libary 5 lượt 20, nhận về "đầy đủ rồi" trong khi bản đồ còn hai nhóm [CHƯA HỎI].
    [Fact]
    public async Task AVerificationSummaryWithoutChipsGetsTheStandardPair()
    {
        const string summary = "Mình xin tóm tắt lại những gì đã chốt: Manager tạo JD, HRBP verify, HoD approve. "
                               + "Mình hiểu đúng chứ ạ?";
        var llm = new FakeLlm(PartialMap) { ChatReply = new BAChatReply { Message = summary } };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "ok");

        Assert.Equal(summary, result.Reply);
        Assert.Equal(BAChatService.SummaryCheckSuggestions, result.Suggestions);
        Assert.False(result.OpenEnded);
    }

    [Fact]
    public async Task WhenTheMapIsComplete_ASilentTurnBecomesTheInviteInstead()
    {
        var llm = new FakeLlm(ReadyMap)
        {
            ChatReply = new BAChatReply { Message = "Mình ghi nhận đầy đủ. Mình tiếp tục sang bước rà soát cuối." }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "tiếp tục đi");

        // Bản đồ đã đủ ⇒ việc duy nhất còn lại là bấm nút thật, và lượt phải NÓI ra điều đó thay vì hứa
        // hẹn một bước của BA. Không phải câu hỏi nên cũng không mời "kể tự do" ở ô nhập.
        Assert.Contains("Write Requirement", result.Reply);
        Assert.True(result.InvitesWriteRequirement);
        Assert.False(result.OpenEnded);
    }

    private async Task<AgentConversation> LastAssistantTurnAsync()
    {
        await using var db = NewDb();
        return await db.AgentConversations
            .Where(c => c.ProjectId == _projectId && c.Role == "assistant")
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .FirstAsync();
    }

    // Cùng harness dựng BAChatService như BAChatRepeatedQuestionTests.
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
            new InterviewOutlookService(db, llm, prompts),
            new InterviewScopeService(db, llm, prompts),
            new ScreenStepPlacementService(llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class FakeLlm : ILlmClient
    {
        private readonly string? _coverageMap;

        public FakeLlm(string? coverageMap) => _coverageMap = coverageMap;

        public BAChatReply ChatReply = new() { Message = "Đã ghi nhận." };

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            if (logContext.Purpose != "BARequirementCoverage")
                return Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

            return Task.FromResult(_coverageMap == null
                ? new LlmCallResult { IsSuccess = false, ErrorMessage = "distill lỗi" }
                : new LlmCallResult { IsSuccess = true, Content = _coverageMap });
        }

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {

            // Bản đồ bao phủ nay đi qua đường structured output (RequirementCoverageService). Fake trả về
            // Value null để service rơi xuống nhánh parse văn xuôi — đúng nhánh mà một model không nhận
            // response_format sẽ chạy — nên các test ở đây vẫn seed bản đồ bằng text như trước.
            if (logContext.Purpose == "BARequirementCoverage")
                return Task.FromResult((ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).Result, (T?)null));
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

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
