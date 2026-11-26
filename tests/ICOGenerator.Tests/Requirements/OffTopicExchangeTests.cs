using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Localization;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Organization;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// CẶP HỎI–ĐÁP NGOÀI PHẠM VI — người dùng hỏi điều không dính gì tới dự án, BA từ chối.
//
// Ca thật (dự án Locker management, 2026-11-25): Brief V1 đã duyệt, POC đã dựng, người dùng gõ "giá vàng
// hôm nay là bao nhiêu". BA không đáp một chữ nào về câu đó; thứ lên màn hình là thẻ "Mình đã thu thập đủ
// thông tin để soạn bản mô tả sản phẩm" kèm nút "Write Requirement". Hai lỗi chồng nhau: cổng tạo tài liệu
// đo "có gì mới từ lần soạn trước" bằng "có lượt user nào không", và lượt trả lời (bản đồ vẫn 11/11) bị
// chốt chặn lượt câm đổi thành lời mời rồi giấu đi. Không có gì về dự án đổi cả, nên không có gì để soạn lại.
//
// Hai luật giữ ở đây: cặp hỏi–đáp ngoài phạm vi TRONG SUỐT với cổng và với mọi bản đúc kết yêu cầu, và lượt
// từ chối do cơ chế dựng (câu cố định + chép lại câu hỏi đang dở), không qua chốt chặn nào của lượt hỏi.
public class OffTopicExchangeTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc);

    // ==== Cổng tạo tài liệu ====

    [Fact]
    public void Gate_StaysDone_WhenTheOnlyTurnsSinceTheDraftAreOffTopic()
    {
        var conversations = new List<AgentConversation>
        {
            Turn("user", "ok, soạn đi", 0),
            Turn("assistant", "", 1, verified: true),
            // Vòng soạn xong ở phút 2, rồi một câu hỏi giá vàng.
            Turn("user", "giá vàng hôm nay là bao nhiêu", 5, offTopic: true),
            Turn("assistant", "Câu này nằm ngoài phạm vi…", 6, offTopic: true),
        };

        var gate = WriteRequirementGate.Resolve(conversations, new[] { DraftRun(finishedMinute: 2) }, briefExists: true, tablePending: false);

        Assert.Equal(WriteRequirementGate.Done, gate.Value);
        Assert.False(gate.IsOpen);
    }

    // Đối chứng: một câu VỀ DỰ ÁN sau lần soạn đúng là "có gì mới" — cổng rời "done" và đi theo lượt BA.
    [Fact]
    public void Gate_LeavesDone_WhenTheUserSaysSomethingAboutTheProject()
    {
        var conversations = new List<AgentConversation>
        {
            Turn("user", "ok, soạn đi", 0),
            Turn("assistant", "", 1, verified: true),
            Turn("user", "thêm màn hình khu vực", 5),
            Turn("assistant", "Khu vực do ai tạo?", 6),
        };

        var gate = WriteRequirementGate.Resolve(conversations, new[] { DraftRun(finishedMinute: 2) }, briefExists: true, tablePending: false);

        Assert.Equal(WriteRequirementGate.Waiting, gate.Value);
    }

    // Lời mời còn nguyên hiệu lực sau một câu ngoài phạm vi: không có gì đổi, nên nút vẫn ở đó.
    [Fact]
    public void Gate_StaysReady_WhenAnOffTopicExchangeFollowsTheInvite()
    {
        var conversations = new List<AgentConversation>
        {
            Turn("user", "không còn gì nữa", 0),
            Turn("assistant", "", 1, verified: true),
            Turn("user", "thời tiết mai thế nào", 2, offTopic: true),
            Turn("assistant", "Câu này nằm ngoài phạm vi…", 3, offTopic: true),
        };

        var gate = WriteRequirementGate.Resolve(conversations, Array.Empty<WorkflowRun>(), briefExists: false, tablePending: false);

        Assert.Equal(WriteRequirementGate.Ready, gate.Value);
        // …và lời mời vẫn không có bong bóng riêng: thẻ cổng ở cuối khung chat nói thay nó.
        Assert.Equal(conversations[1].Id, RequirementReadinessGate.InviteTurnToHide(conversations, tableGatePending: false)?.Id);
        // Bước soạn tài liệu cũng thấy hội thoại đang đứng trên lời mời đã verify.
        Assert.True(RequirementReadinessGate.IsReadinessVerifiedLatestTurn(conversations));
    }

    // ==== Lượt từ chối ====

    [Fact]
    public void Shape_CarriesOverThePendingSingleQuestionWithItsChips()
    {
        var chips = JsonSerializer.Serialize(new[] { "FCM cấp tủ", "Supervisor tự cấp" });
        var prior = new List<AgentConversation>
        {
            Turn("user", "visitor cần nhờ internal request", 0),
            Turn("assistant", "Sau khi Supervisor duyệt, ai là người cấp tủ?", 1, suggestions: chips),
        };

        var draft = new BAChatTurnDraft();
        OffTopicExchange.Shape(draft, OffTopicExchange.PendingAsk(prior));

        Assert.True(draft.OffTopic);
        Assert.False(draft.InvitesWriteRequirement);
        Assert.StartsWith(Loc.T("OffTopicExchange.Decline"), draft.Reply);
        Assert.Contains("Sau khi Supervisor duyệt, ai là người cấp tủ?", draft.Reply);
        Assert.Equal(chips, draft.SuggestionsJson);
    }

    [Fact]
    public void Shape_CarriesOverThePendingBatchCard()
    {
        var questions = JsonSerializer.Serialize(new List<BAChatQuestion>
        {
            new() { Question = "Tủ có mấy cỡ?", Suggestions = new() { "Một cỡ", "Nhiều cỡ" } },
            new() { Question = "Có giới hạn thời gian giữ tủ không?", Suggestions = new() { "Có", "Không" } },
        });
        var prior = new List<AgentConversation> { Turn("assistant", "Mình hỏi nhanh mấy điểm:", 0, questions: questions) };

        var draft = new BAChatTurnDraft();
        OffTopicExchange.Shape(draft, OffTopicExchange.PendingAsk(prior));

        Assert.Equal(2, draft.Questions.Count);
        Assert.Null(draft.SuggestionsJson);
        Assert.StartsWith(Loc.T("OffTopicExchange.Decline"), draft.Reply);
    }

    // Không có câu nào đang dở ⇒ câu từ chối + một câu mời quay lại dự án, không chip, không hỏi gì. Lời mời,
    // lượt bày bảng và lượt user chưa có lượt BA nối tiếp đều KHÔNG phải câu đang dở.
    [Theory]
    [InlineData("invite")]
    [InlineData("table")]
    [InlineData("user-last")]
    public void PendingAsk_IsNull_WhenTheUserOwesNoAnswer(string shape)
    {
        var prior = shape switch
        {
            "invite" => new List<AgentConversation> { Turn("assistant", "", 0, verified: true) },
            "table" => new List<AgentConversation> { Turn("assistant", "Anh/chị rà bảng luồng giúp mình.", 0, flowMap: "[{}]") },
            _ => new List<AgentConversation> { Turn("assistant", "Ai cấp tủ?", 0), Turn("user", "FCM", 1) },
        };

        Assert.Null(OffTopicExchange.PendingAsk(prior));

        var draft = new BAChatTurnDraft();
        OffTopicExchange.Shape(draft, null);
        Assert.Equal(Loc.T("OffTopicExchange.Decline") + " " + Loc.T("OffTopicExchange.Steer"), draft.Reply);
        Assert.Null(draft.SuggestionsJson);
        Assert.Empty(draft.Questions);
    }

    // Một câu ngoài phạm vi chen giữa không làm câu đang dở "hết dở": hỏi giá vàng hai lần liền thì lần thứ
    // hai vẫn chép lại đúng câu hỏi gốc.
    [Fact]
    public void PendingAsk_LooksThroughEarlierOffTopicExchanges()
    {
        var prior = new List<AgentConversation>
        {
            Turn("assistant", "Ai cấp tủ?", 0),
            Turn("user", "giá vàng?", 1, offTopic: true),
            Turn("assistant", "Câu này nằm ngoài phạm vi…", 2, offTopic: true),
        };

        Assert.Same(prior[0], OffTopicExchange.PendingAsk(prior));
    }

    [Fact]
    public void Normalize_StripsEverythingButTheFlag()
    {
        var reply = new BAChatReplyParser().Normalize(new BAChatReply
        {
            Message = "Giá vàng hôm nay khoảng 85 triệu/lượng.",
            Suggestions = new() { "A", "B" },
            Ready = true,
            OffTopic = true
        });

        Assert.True(reply.OffTopic);
        Assert.False(reply.Ready);
        Assert.Empty(reply.Suggestions);
        Assert.Empty(reply.Questions);
    }

    // ==== Prompt + golden set ====

    // Cờ chỉ có nghĩa khi CẢ HAI prompt dạy nó: chế độ yêu cầu thay đổi (sau Approve) là đúng chế độ của ca
    // thật, và nó chạy prompt riêng.
    [Theory]
    [InlineData("BusinessAnalyst/requirement-chat.v4.md")]
    [InlineData(BAChatService.ChangeRequestPromptKey)]
    public void BothChatPrompts_TeachTheOffTopicTurn(string promptKey)
    {
        var prompt = ReadPrompt(promptKey);

        Assert.Contains("\"offTopic\": true", prompt, StringComparison.Ordinal);
        Assert.Contains("Không trả lời câu hỏi đó", prompt, StringComparison.OrdinalIgnoreCase);
        // Ranh giới phía ngược lại: câu trả lời cụt không bao giờ là câu ngoài phạm vi.
        Assert.Contains("không biết", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // Prompt chỉ định hướng; điểm eval mới đo được model có khai cờ đúng không — ở CẢ HAI phía ranh giới.
    [Fact]
    public void GoldenSet_ScoresBothSidesOfTheBoundary()
    {
        var criteria = EvalScenariosSeedData.Build()
            .Where(s => s.PromptKey == "BusinessAnalyst/requirement-chat.v4.md")
            .Select(s => s.Criteria)
            .ToList();

        Assert.Contains(criteria, c => c.Contains("offTopic PHẢI là true", StringComparison.Ordinal));
        Assert.Contains(criteria, c => c.Contains("offTopic KHÔNG được là true", StringComparison.Ordinal));
    }

    // ==== Các bản đúc kết yêu cầu ====

    [Fact]
    public void BriefTranscript_LeavesTheOffTopicExchangeOut()
    {
        var transcript = ConversationTranscriptBuilder.Build(new[]
        {
            Turn("assistant", "Ai cấp tủ?", 0),
            Turn("user", "FCM cấp", 1),
            Turn("user", "giá vàng hôm nay là bao nhiêu", 2, offTopic: true),
            Turn("assistant", "Câu này nằm ngoài phạm vi…", 3, offTopic: true),
        });

        Assert.Contains("FCM cấp", transcript);
        Assert.DoesNotContain("giá vàng", transcript);
        Assert.DoesNotContain("ngoài phạm vi", transcript);
    }

    // ==== Trọn một lượt chat ====

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    // Bản đồ ĐỦ: đúng điều kiện đã biến lượt trả lời câu giá vàng thành một lời mời.
    private static readonly string ReadyMap =
        CoverageMapFixture.DistillReply("- ★ Mục tiêu / bài toán: [RÕ] quản lý cấp tủ cho visitor.\n"
        + "- ★ Chức năng & luồng nghiệp vụ chính: [RÕ] request–duyệt–cấp tủ.");

    public OffTopicExchangeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = _model.Id });
        db.Projects.Add(new Project { Id = _projectId, Name = "P", Description = "app quản lý tủ" });
        db.SaveChanges();
    }

    [Fact]
    public async Task ChatAsync_OffTopicWithAPendingQuestion_DeclinesAndAsksItAgain()
    {
        var chips = JsonSerializer.Serialize(new[] { "FCM cấp tủ", "Supervisor tự cấp" });
        await SeedAsync(Turn("assistant", "Sau khi Supervisor duyệt, ai là người cấp tủ?", -10, suggestions: chips));

        // Model khai đúng cờ nhưng lỡ trả lời luôn, và còn khai `ready` — cả hai phải bị bỏ.
        var llm = new FakeLlm(ReadyMap)
        {
            ChatReply = new BAChatReply { Message = "Giá vàng hôm nay khoảng 85 triệu/lượng.", Ready = true, OffTopic = true }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "giá vàng hôm nay là bao nhiêu");

        Assert.True(result.OffTopic);
        Assert.False(result.InvitesWriteRequirement);
        Assert.DoesNotContain("85 triệu", result.Reply);
        Assert.StartsWith(Loc.T("OffTopicExchange.Decline"), result.Reply);
        Assert.Contains("ai là người cấp tủ?", result.Reply);
        Assert.Equal(new[] { "FCM cấp tủ", "Supervisor tự cấp" }, result.Suggestions);

        var (user, assistant) = await LastPairAsync();
        Assert.True(user.OffTopic);
        Assert.True(assistant.OffTopic);
        Assert.False(assistant.ReadinessVerified);
        Assert.Equal(result.Reply, assistant.Message);
    }

    // Đúng ca trên màn hình: lượt BA cuối là lời mời, bản đồ đủ. Trước đây lượt này hoặc là một lời mời
    // (model khai ready), hoặc là lời từ chối không dấu hỏi bị chốt chặn lượt câm đổi thành lời mời — cả hai
    // đều kết thúc ở một cái nút thay cho câu trả lời.
    [Fact]
    public async Task ChatAsync_OffTopicAfterTheInvite_IsNotTurnedIntoAnotherInvite()
    {
        await SeedAsync(Turn("assistant", "", -10, verified: true));

        var llm = new FakeLlm(ReadyMap) { ChatReply = new BAChatReply { OffTopic = true } };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "giá vàng hôm nay là bao nhiêu");

        Assert.Equal(Loc.T("OffTopicExchange.Decline") + " " + Loc.T("OffTopicExchange.Steer"), result.Reply);
        Assert.False(result.InvitesWriteRequirement);
        Assert.Empty(result.Suggestions);
    }

    // Sửa câu giá vàng thành một câu về dự án ⇒ dấu "ngoài phạm vi" của lượt user phải rơi theo, nếu không câu
    // sửa bị loại khỏi transcript soạn Brief.
    [Fact]
    public async Task EditingAnOffTopicTurn_IntoARealAnswer_ClearsTheFlag()
    {
        await SeedAsync(Turn("assistant", "Ai là người cấp tủ?", -10));

        var llm = new FakeLlm(ReadyMap) { ChatReply = new BAChatReply { OffTopic = true } };
        await using (var db = NewDb())
            await NewSut(db, llm).ChatAsync(_projectId, "giá vàng hôm nay là bao nhiêu");

        llm.ChatReply = new BAChatReply { Message = "Tủ cấp theo thứ tự nào?", Suggestions = new() { "Ai đến trước", "Theo ưu tiên" } };
        await using (var db = NewDb())
            await NewSut(db, llm).EditLastUserTurnAsync(_projectId, "FCM cấp tủ");

        var (user, assistant) = await LastPairAsync();
        Assert.Equal("FCM cấp tủ", user.Message);
        Assert.False(user.OffTopic);
        Assert.False(assistant.OffTopic);
    }

    // ==== Hạ tầng ====

    private static AgentConversation Turn(
        string role, string message, int minute,
        bool verified = false, bool offTopic = false,
        string? suggestions = null, string? questions = null, string? flowMap = null)
        => new()
        {
            Role = role,
            Message = message,
            CreatedAt = T0.AddMinutes(minute),
            ReadinessVerified = verified,
            OffTopic = offTopic,
            Suggestions = suggestions,
            Questions = questions,
            FlowMap = flowMap
        };

    private static WorkflowRun DraftRun(int finishedMinute) => new()
    {
        Kind = WorkflowRunKind.RequirementDraft,
        Name = WorkflowOrchestrator.RequirementDraftRunName,
        Status = WorkflowRunStatus.Completed,
        CreatedAt = T0.AddMinutes(finishedMinute - 1),
        FinishedAt = T0.AddMinutes(finishedMinute)
    };

    // Lượt seed đứng TRƯỚC lượt user mà ChatAsync ghi lúc chạy (CreatedAt = bây giờ), nên mốc thời gian của
    // nó tính từ bây giờ chứ không từ T0.
    private async Task SeedAsync(AgentConversation turn)
    {
        await using var db = NewDb();
        turn.ProjectId = _projectId;
        turn.AgentId = _baId;
        turn.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
        db.AgentConversations.Add(turn);
        await db.SaveChangesAsync();
    }

    private async Task<(AgentConversation User, AgentConversation Assistant)> LastPairAsync()
    {
        await using var db = NewDb();
        var turns = await db.AgentConversations
            .Where(c => c.ProjectId == _projectId)
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Take(2)
            .ToListAsync();
        return (turns[1], turns[0]);
    }

    // Cùng harness dựng BAChatService như BAChatSilentTurnTests.
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
            new ReportMapDraftService(db, llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
    }

    // Cùng cách tìm Prompts/ như BAChatPlaybackRuleTests: ưu tiên bản copy trong bin, không có thì đi ngược
    // lên repo root.
    private static string ReadPrompt(string promptKey)
    {
        var relative = promptKey.Replace('/', Path.DirectorySeparatorChar);

        var fromBin = Path.Combine(AppContext.BaseDirectory, "Prompts", relative);
        if (File.Exists(fromBin))
            return File.ReadAllText(fromBin);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Prompts", relative);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("Không tìm thấy prompt " + promptKey + " từ " + AppContext.BaseDirectory);
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
            if (logContext.Purpose == "BARequirementCoverage")
                return Task.FromResult((ChatWithLogAsync(model, messages, logContext, onToken, cancellationToken).Result, (T?)null));
            if (logContext.Purpose != "BAChat")
                throw new InvalidOperationException($"Unexpected structured call: {logContext.Purpose}");

            // Bản sao MỚI mỗi lượt: Normalize sửa tại chỗ, mà một test chạy hai lượt trên cùng một fake.
            var copy = JsonSerializer.Deserialize<BAChatReply>(JsonSerializer.Serialize(ChatReply))!;
            return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)(object)copy));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }
}
