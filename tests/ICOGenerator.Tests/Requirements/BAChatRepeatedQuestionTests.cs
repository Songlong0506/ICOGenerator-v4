using System.Text.Json;
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

// KHÔNG HỎI LẠI ĐIỀU NGƯỜI DÙNG VỪA TRẢ LỜI — ở mức lượt chat, không chỉ ở prompt.
//
// Bệnh gốc: bản đồ bao phủ là thứ DUY NHẤT dẫn dắt lượt hỏi, mà nó chỉ có độ phân giải theo NHÓM. Một
// dòng chưa đạt chuẩn [RÕ] (chuẩn cố ý khắt khe) đồng nghĩa "ưu tiên hỏi nhóm này", và vì mỗi câu hỏi
// của lượt gộp được gắn `group` = tên dòng bản đồ, model phát lại đúng câu hỏi mở đầu của nhóm đó —
// người dùng vừa trả lời xong đã bị hỏi lại nguyên văn, chip gợi ý chính là câu họ vừa gõ. Cùng triệu
// chứng khi lượt chắt lọc bản đồ hỏng (fail-open giữ bản cũ): cả cụm câu hỏi lượt trước được phát lại.
//
// Prompt đã cấm, nhưng prompt chỉ định hướng. Các bất biến dưới đây mới là cái phanh.
public class BAChatRepeatedQuestionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    private const string AskedRoles = "Ai sẽ sử dụng app này và vai trò của họ?";
    private const string AskedNotify = "Khi có nhân viên đạt 11 giờ, cách thức nhắc nhở và hành động tiếp theo ra sao?";

    // Bản đồ mà lượt chắt lọc (fake) trả về ở mọi test: hai nhóm người dùng VỪA trả lời vẫn bị giữ
    // [MỘT PHẦN] — đúng tình huống đã đẻ ra bệnh, vì đó là lúc prompt bảo BA "ưu tiên hỏi nhóm này".
    private const string PartialMap =
        "- ★ Mục tiêu / bài toán: [RÕ] hiển thị nhân viên làm quá 11 giờ. {nguồn: \"nhắc đi về trước 12 tiếng\"}\n"
        + "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: quan hệ cấp trên của các vai trò\n"
        + "- Thông báo / nhắc nhở: [MỘT PHẦN] còn thiếu: khi nào thì gọi";

    public BAChatRepeatedQuestionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "app giờ làm việc" });
        db.SaveChanges();
    }

    [Fact]
    public async Task RepeatedQuestionsAreDroppedFromTheBatch_OnlyTheNewOneSurvives()
    {
        await SeedAnsweredBatchAsync();

        // Lượt "xác nhận lại" kinh điển: hai câu cũ nguyên văn + một câu thật sự mới.
        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply
            {
                Message = "Cảm ơn bạn. Dưới đây là 3 câu xác nhận:",
                Questions = new List<BAChatQuestion>
                {
                    new() { Group = "Đối tượng người dùng & vai trò", Question = "Ai sẽ dùng app và vai trò của họ?", Suggestions = new List<string> { "Phòng bảo vệ", "Phòng nhân sự" } },
                    new() { Group = "Thông báo / nhắc nhở", Question = AskedNotify, Suggestions = new List<string> { "Gọi điện", "Gọi manager" } },
                    new() { Group = "Quy mô sử dụng", Question = "Áng chừng bao nhiêu nhân viên trong nhà máy?", Suggestions = new List<string> { "Dưới 500", "Trên 500" } }
                }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Phòng bảo vệ xem dashboard, phòng nhân sự xem history");

        Assert.Equal(ChatWithBAResult.Ok, result.Status);
        // Còn đúng một câu MỚI ⇒ hạ về đường một-câu: câu hỏi lên thẳng nội dung lượt, gợi ý riêng của nó
        // được nâng lên làm chip. Người dùng không thấy bóng dáng nào của hai câu cũ.
        Assert.Empty(result.Questions);
        Assert.Equal("Áng chừng bao nhiêu nhân viên trong nhà máy?", result.Reply);
        Assert.Equal(new[] { "Dưới 500", "Trên 500" }, result.Suggestions);
        Assert.DoesNotContain("vai trò của họ", result.Reply);

        var saved = await LastAssistantTurnAsync();
        Assert.Equal("Áng chừng bao nhiêu nhân viên trong nhà máy?", saved.Message);
        Assert.Empty(ConversationTurnRenderer.ParseQuestions(saved.Questions));
    }

    [Fact]
    public async Task WhenEveryQuestionIsARepeat_TheTurnBecomesTheDeterministicFollowUp()
    {
        await SeedAnsweredBatchAsync();

        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply
            {
                Message = "Cảm ơn bạn. Mình xác nhận lại mấy điểm sau:",
                Questions = new List<BAChatQuestion>
                {
                    new() { Group = "Đối tượng người dùng & vai trò", Question = AskedRoles, Suggestions = new List<string> { "Phòng bảo vệ" } },
                    new() { Group = "Thông báo / nhắc nhở", Question = AskedNotify, Suggestions = new List<string> { "Gọi điện" } }
                }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Phòng bảo vệ xem dashboard, phòng nhân sự xem history");

        // Không im lặng và không để lại câu dẫn cụt ("Mình xác nhận lại mấy điểm sau:" mà chẳng có điểm
        // nào): lượt được thay bằng bước kế tiếp suy TẤT ĐỊNH từ bản đồ — hỏi ĐÚNG phần còn thiếu của
        // dòng ★ cốt lõi đang treo, và chỉ MỘT chỗ (chỗ còn lại để dành lượt sau, không hỏi dồn).
        Assert.Empty(result.Questions);
        Assert.DoesNotContain("xác nhận lại mấy điểm sau", result.Reply);
        Assert.Contains("quan hệ cấp trên của các vai trò", result.Reply);
        Assert.DoesNotContain("khi nào thì gọi", result.Reply);

        // …và lượt đó KHÔNG đọc sổ sách của hệ thống ra màn hình: không nhãn nhóm, không đếm nhóm còn lại.
        Assert.DoesNotContain("nhóm", result.Reply, StringComparison.OrdinalIgnoreCase);

        var saved = await LastAssistantTurnAsync();
        Assert.Equal(result.Reply, saved.Message);
    }

    [Fact]
    public async Task ASingleQuestionTurnThatRepeatsIsReplacedToo()
    {
        await SeedAnsweredBatchAsync();

        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply
            {
                Message = AskedRoles,
                Suggestions = new List<string> { "Phòng bảo vệ", "Phòng nhân sự" }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Phòng bảo vệ xem dashboard, phòng nhân sự xem history");

        Assert.NotEqual(AskedRoles, result.Reply);
        Assert.Contains("quan hệ cấp trên của các vai trò", result.Reply);
        Assert.DoesNotContain("Đối tượng người dùng & vai trò", result.Reply);
    }

    [Fact]
    public async Task AGroupTheUserReopenedMayBeAskedAgain()
    {
        await SeedAnsweredBatchAsync();

        // Người dùng vừa nói trong chat "nhóm vai trò chưa đúng" và lượt chắt lọc đã đánh dấu dòng đó ⇒ họ
        // CHỦ ĐỘNG xin được hỏi lại. Phanh phải nhường, nếu không lời đính chính của họ rơi vào im lặng:
        // bản đồ đã hạ nhóm xuống nhưng câu hỏi của BA lại bị lọc mất vì trùng câu cũ.
        var reopenedMap =
            "- ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] còn thiếu: " + AskedQuestionHistory.ReopenNote + " — cần hỏi lại và chốt lại.\n"
            + "- Thông báo / nhắc nhở: [MỘT PHẦN] còn thiếu: khi nào thì gọi";

        var llm = new FakeLlm(reopenedMap)
        {
            ChatReply = new BAChatReply
            {
                Message = "Mình hỏi lại mấy điểm sau nhé:",
                Questions = new List<BAChatQuestion>
                {
                    new() { Group = "Đối tượng người dùng & vai trò", Question = AskedRoles, Suggestions = new List<string> { "Phòng bảo vệ" } },
                    new() { Group = "Thông báo / nhắc nhở", Question = AskedNotify, Suggestions = new List<string> { "Gọi điện" } }
                }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Nhóm vai trò chưa đúng");

        // Câu của nhóm ĐƯỢC MỞ LẠI sống sót; câu của nhóm còn lại vẫn bị loại vì đã hỏi rồi ⇒ còn một câu
        // ⇒ hạ về đường một-câu.
        Assert.Empty(result.Questions);
        Assert.Equal(AskedRoles, result.Reply);
    }

    [Fact]
    public async Task ANewQuestionOnAPartialGroupIsNotBlocked()
    {
        await SeedAnsweredBatchAsync();

        // Đúng việc BA phải làm với nhóm [MỘT PHẦN]: hỏi phần "còn thiếu:", KHÔNG phát lại câu mở đầu.
        const string followUp = "Anh/chị vừa nói phòng bảo vệ gọi điện nhắc — cuộc gọi đó nổ ra ngay lúc chạm 11 giờ hay tới ca trực mới rà một lượt?";
        var llm = new FakeLlm(PartialMap)
        {
            ChatReply = new BAChatReply { Message = followUp, Suggestions = new List<string> { "Ngay lúc chạm 11 giờ", "Theo ca trực" } }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Phòng bảo vệ gọi điện nhắc");

        Assert.Equal(followUp, result.Reply);
    }

    [Fact]
    public async Task PreviouslyAskedQuestionsAreLoadedIntoTheBaContext()
    {
        await SeedAnsweredBatchAsync();

        var llm = new FakeLlm(PartialMap) { ChatReply = new BAChatReply { Message = "Nhà máy có bao nhiêu nhân viên?", Suggestions = new List<string> { "Dưới 500" } } };

        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "Phòng bảo vệ xem dashboard");

        // Nửa còn lại của phanh: model phải ĐỌC được danh sách câu đã hỏi, không chỉ bị lọc sau khi lỡ hỏi.
        var context = string.Join("\n", llm.LastChatSystemMessages);
        Assert.Contains("Các câu hỏi BẠN ĐÃ HỎI", context);
        Assert.Contains(AskedRoles, context);
        Assert.Contains(AskedNotify, context);
    }

    [Fact]
    public async Task WhenTheCoverageDistillFails_TheTurnReportsTheMapIsStale()
    {
        await SeedAnsweredBatchAsync();

        // Bản đồ đứng im là chuyện người dùng phải thấy: BA vừa dẫn lượt bằng bản đồ CŨ, nên nó có thể
        // hỏi lại đúng nhóm vừa được trả lời — triệu chứng trông hệt "BA không nghe mình nói".
        var llm = new FakeLlm(coverageMap: null) { ChatReply = new BAChatReply { Message = "Nhà máy có bao nhiêu nhân viên?", Suggestions = new List<string> { "Dưới 500" } } };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Phòng bảo vệ xem dashboard");

        Assert.True(result.CoverageStale);
        Assert.Equal(2, llm.CoverageCalls); // đã thử lại một lần trước khi chịu thua
    }

    // Hội thoại nền của mọi test: BA hỏi GỘP hai câu, người dùng trả lời cả hai trong một lượt (đúng
    // định dạng "- câu hỏi: trả lời" mà thẻ hỏi gộp sinh ra).
    private async Task SeedAnsweredBatchAsync()
    {
        await using var db = NewDb();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "user",
            Message = "Cần app hiển thị nhân viên làm quá 11 giờ trong nhà máy",
            CreatedAt = baseTime
        });
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "assistant",
            Message = "Cảm ơn. Mình hỏi 2 điểm sau nhé:",
            Questions = JsonSerializer.Serialize(new[]
            {
                new BAChatQuestion { Group = "Đối tượng người dùng & vai trò", Question = AskedRoles, Suggestions = new List<string> { "Phòng bảo vệ" } },
                new BAChatQuestion { Group = "Thông báo / nhắc nhở", Question = AskedNotify, Suggestions = new List<string> { "Gọi điện" } }
            }),
            CreatedAt = baseTime.AddSeconds(1)
        });
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId,
            AgentId = _baId,
            Role = "user",
            Message = $"- {AskedRoles}: Phòng bảo vệ xem dashboard, phòng nhân sự xem history\n"
                      + $"- {AskedNotify}: Gọi điện cho nhân viên, không được thì gọi manager",
            CreatedAt = baseTime.AddSeconds(2)
        });
        await db.SaveChangesAsync();
    }

    private async Task<AgentConversation> LastAssistantTurnAsync()
    {
        await using var db = NewDb();
        return await db.AgentConversations
            .Where(c => c.ProjectId == _projectId && c.Role == "assistant")
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .FirstAsync();
    }

    // Cùng harness dựng BAChatService như BAChatRetryTests (không scope factory ⇒ các bước chuẩn bị chạy
    // tuần tự trên chính db của test).
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
            new RequirementCoverageService(db, llm, prompts),
            new OrganizationContextService(db, prompts, new MemoryCache(new MemoryCacheOptions()), NullLogger<OrganizationContextService>.Instance),
            new BAAgentResolver(db),
            new BAConversationLog(db),
            new DecisionLogService(db, llm, prompts),
            new InterviewOutlookService(db, llm, prompts),
            new ScreenStepPlacementService(llm, prompts),
            new ChecklistNoteStore(db),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // coverageMap = null ⇒ lượt chắt lọc bản đồ LỖI (fail-open). Mọi lời gọi text khác (bộ nhớ, hồ sơ
    // user, decision log) cũng fail-open nên không cần dựng riêng.
    private sealed class FakeLlm : ILlmClient
    {
        private readonly string? _coverageMap;

        public FakeLlm(string? coverageMap) => _coverageMap = coverageMap;

        public BAChatReply ChatReply = new() { Message = "Đã ghi nhận." };
        public int CoverageCalls;
        public List<string> LastChatSystemMessages = new();

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
        {
            if (logContext.Purpose != "BARequirementCoverage")
                return Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

            CoverageCalls++;
            return Task.FromResult(_coverageMap == null
                ? new LlmCallResult { IsSuccess = false, ErrorMessage = "distill lỗi" }
                : new LlmCallResult { IsSuccess = true, Content = _coverageMap });
        }

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            if (logContext.Purpose != "BAChat")
                throw new InvalidOperationException($"Unexpected structured call: {logContext.Purpose}");

            LastChatSystemMessages = messages
                .Where(m => m.Role == ChatRole.System)
                .Select(m => m.Text ?? string.Empty)
                .ToList();

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
