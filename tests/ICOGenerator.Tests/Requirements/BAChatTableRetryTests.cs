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
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// LƯỢT BÀY BẢNG MÀ MODEL QUÊN TRẢ BẢNG — và vì sao nó không tự đỡ được như fail-open dự tính.
//
// Lượt bày bảng bị prompt ép vào một hình dạng rất hẹp: `message` một câu mời rà bảng, `suggestions` và
// `questions` rỗng, không kết bằng dấu hỏi. Hình dạng đó TRÙNG KHÍT với LƯỢT CÂM (BAChatTurnDraft.IsSilent).
// Nên khi model viết đúng câu dẫn mà bỏ quên trường bảng, lượt ấy không "chạy như một lượt chat thường":
// nó rơi thẳng vào chốt chặn lượt câm và bị thay bằng một câu hỏi của cổng bao phủ — lạc hẳn khỏi việc BA
// vừa hứa ở chính câu đó.
//
// Ca thật (log BAChat 2026-09-08 03:35:04 UTC, deepseek-v4-flash): cổng bảng đối tượng mở, model trả đúng
// 168 token gồm mỗi câu *"…Anh/chị vui lòng xem bảng bên dưới và bấm nút \"Gửi bảng đối tượng\"…"* và
// KHÔNG có `entityMap`. Thứ được lưu và hiện lên màn hình là câu phát lại của cổng bao phủ về nhóm «Thông
// báo / nhắc nhở» — người dùng đọc log thấy model nói một đằng, UI hiện một nẻo, còn câu của model thì
// không bao giờ tới được ai.
//
// Chốt chặn ở đây đứng TRƯỚC cả hai: đòi model trả lại bảng ngay trong lượt, tối đa
// BAChatService.MaxTableAttempts lời gọi, rồi mới để lượt đi tiếp.
public class BAChatTableRetryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    // Bản đồ cuối buổi + bảng luồng đã chốt = đúng trạng thái mở cổng BẢNG ĐỐI TƯỢNG (xem InterviewTableGateTests).
    private static readonly string EverythingClear = CoverageMapFixture.Map("""
        - ★ Mục tiêu / bài toán: [RÕ] Lập kế hoạch đào tạo.
        - ★ Đối tượng người dùng & vai trò: [RÕ] HR Assistant lập, HOD HR duyệt.
        - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Tạo plan, submit theo quý.
        - Quy trình hiện tại & điểm khó: [RÕ] Làm tay trên Excel.
        - Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] HOD từ chối thì Assistant sửa lại.
        - Dữ liệu / danh mục chính: [RÕ] Khóa học, người học, đơn vị.
        - Quy tắc nghiệp vụ & ràng buộc: [RÕ] Sĩ số tối đa 20.
        - Vòng đời & trạng thái: [RÕ] Nháp → Chờ duyệt → Đã duyệt.
        - Thông báo / nhắc nhở: [MỘT PHẦN] Báo HOD khi submit.
        - Báo cáo / thống kê: [KHÔNG ÁP DỤNG] Chưa cần.
        - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
        - Quy mô sử dụng: [RÕ] Khoảng 200 người.
        """);

    private const string ConfirmedFlow = """
        [{"name":"Lập kế hoạch","kind":"luồng chính","role":"HR Assistant","steps":[
          {"actor":"HR Assistant","action":"Tạo kế hoạch quý","outcome":"Nháp","included":true},
          {"actor":"HOD HR","action":"Duyệt kế hoạch","outcome":"Đã duyệt","included":true}]}]
        """;

    // Nguyên văn hình dạng của ca thật: câu dẫn ĐÚNG như prompt dặn (một câu, mời bấm nút, không dấu hỏi,
    // không chip) — và không có bảng nào đi kèm.
    private static BAChatReply TableForgotten() => new()
    {
        Message = "Giờ mình sẽ chuyển sang bước chốt các đối tượng nghiệp vụ mà ứng dụng cần lưu hồ sơ. "
                  + "Anh/chị vui lòng xem bảng bên dưới và bấm nút \"Gửi bảng đối tượng\" để xác nhận giúp mình ạ."
    };

    private static BAChatReply TableReturned() => new()
    {
        Message = "Anh/chị rà giúp bảng bên dưới rồi bấm \"Gửi bảng đối tượng\" nhé.",
        EntityMap = new List<EntityMapRow>
        {
            new()
            {
                Entity = "Training Plan",
                Description = "Kế hoạch lớp học theo quý",
                Fields = new List<EntityFieldNote> { new() { Name = "Quarter", Meaning = "Quý áp dụng" } },
                States = new List<EntityLifecycleState> { new() { State = "Draft", EntryCondition = "vừa tạo" } }
            }
        }
    };

    public BAChatTableRetryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, Temperature = 0.2, AiModelId = _model.Id });
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Training Plan",
            Description = "lập kế hoạch đào tạo",
            RequirementCoverageMap = EverythingClear,
            FlowMap = ConfirmedFlow
        });
        db.SaveChanges();
    }

    // Lượt đầu quên bảng ⇒ đòi lại, và lần thứ hai model trả bảng thì BẢNG ĐÓ là thứ ra màn hình. Không có
    // vòng đòi lại thì lượt này kết thúc bằng câu chặn của cổng bao phủ, đúng ca thật.
    [Fact]
    public async Task AMissingTableIsDemandedAgain_AndTheSecondAnswerReachesTheUser()
    {
        var llm = new FakeLlm(EverythingClear, TableForgotten(), TableReturned());

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Đúng rồi, chốt vậy");

        Assert.Equal(2, llm.ChatCalls);
        var entity = Assert.Single(result.EntityMap);
        Assert.Equal("Training Plan", entity.Entity);

        // Và lượt ra màn hình là câu dẫn của LẦN TRẢ LỜI CÓ BẢNG (luật "câu dẫn model thắng" của
        // TakeOverForTable), không phải câu phát lại của cổng bao phủ đã cướp lượt ở ca thật.
        Assert.Equal(TableReturned().Message, result.Reply);
        Assert.DoesNotContain("Mình đang ghi nhận", result.Reply, StringComparison.Ordinal);
    }

    // Lời đòi lại phải NỐI THÊM vào cuối ngữ cảnh cũ (giữ tiền tố dài cho cache của endpoint), gồm bản trả
    // lời hỏng dưới vai assistant rồi mới tới lời đòi — và phải gọi ĐÚNG TÊN trường còn thiếu.
    [Fact]
    public async Task TheDemandNamesTheMissingField_AndQuotesTheBrokenAnswerBack()
    {
        var llm = new FakeLlm(EverythingClear, TableForgotten(), TableReturned());

        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "ok");

        var second = llm.ChatMessages[1];
        var first = llm.ChatMessages[0];
        Assert.Equal(first.Count + 2, second.Count);

        var demand = second[^1];
        Assert.Equal(ChatRole.User, demand.Role);
        Assert.Contains("`entityMap`", demand.Text, StringComparison.Ordinal);
        Assert.Equal(ChatRole.Assistant, second[^2].Role);

        // Lời đòi phải cấm luôn đường "gỡ" bằng một câu hỏi — đó chính là hình dạng mà chốt chặn lượt câm
        // sẽ bắt, nên nó không gỡ được gì cả.
        Assert.Contains("`suggestions`", demand.Text, StringComparison.Ordinal);
        Assert.Contains("ĐỪNG thay bảng bằng một câu hỏi", demand.Text, StringComparison.Ordinal);
    }

    // Trần đòi lại là trần THẬT: model bướng đến cùng thì lượt vẫn phải kết thúc, không quay vòng vô hạn.
    // Hết trần thì fail-open như cũ — cổng bảng đọc bản đồ bao phủ chứ không đọc lịch sử lượt nên nó mở lại
    // ở lượt sau, và chốt chặn lượt câm đỡ lượt này để nó vẫn có chỗ trả lời.
    [Fact]
    public async Task TheDemandStopsAtTheCap_AndTheTurnStillEndsWithSomethingAnswerable()
    {
        var forgetful = Enumerable.Range(0, BAChatService.MaxTableAttempts + 3)
            .Select(_ => TableForgotten())
            .ToArray();
        var llm = new FakeLlm(EverythingClear, forgetful);

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "ok");

        Assert.Equal(BAChatService.MaxTableAttempts, llm.ChatCalls);
        Assert.Empty(result.EntityMap);

        // Fail-open: lượt không có bảng, nhưng cũng không được là một lượt câm.
        Assert.EndsWith("?", result.Reply.TrimEnd(), StringComparison.Ordinal);
        Assert.True(result.OpenEnded);
    }

    // Lượt chat THƯỜNG không đụng tới vòng này: đúng một lời gọi như trước, và không có lời đòi nào được
    // nối thêm. Đây là ranh giới đắt nhất của thay đổi — đòi lại nhầm ở lượt thường là nhân bốn chi phí và
    // độ chờ của MỌI lượt phỏng vấn.
    [Fact]
    public async Task AnOrdinaryChatTurnStillCallsTheModelExactlyOnce()
    {
        await using (var seed = NewDb())
        {
            // Bảng đối tượng đã chốt ⇒ cổng đóng ⇒ lượt này là lượt chat thường.
            var project = await seed.Projects.FirstAsync(p => p.Id == _projectId);
            project.EntityMap = """[{"entity":"Training Plan","included":true,"confirmedByUser":true}]""";
            await seed.SaveChangesAsync();
        }

        var llm = new FakeLlm(EverythingClear, new BAChatReply
        {
            Message = "Anh/chị kể giúp mình khi HOD từ chối thì bước tiếp theo là gì?"
        });

        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "ok");

        Assert.Equal(1, llm.ChatCalls);
    }

    // Lượt bày bảng KHÔNG stream: bong bóng đang gõ chỉ biết CỘNG chữ, nên bản nháp của lần hỏng sẽ dính
    // lại rồi lần sau nối tiếp vào nó. Người dùng nhận dòng trạng thái gọi đúng tên việc thay vào đó.
    [Fact]
    public async Task ATableTurnDoesNotStreamTokens_AndSaysWhatItIsBuilding()
    {
        var llm = new FakeLlm(EverythingClear, TableForgotten(), TableReturned()) { EmitToken = "xin chào" };
        var streamed = new List<string>();
        var status = new List<string>();

        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "ok", status.Add, streamed.Add);

        Assert.Empty(streamed);
        Assert.Contains(status, s => s.Contains("bảng đối tượng nghiệp vụ", StringComparison.Ordinal));
        Assert.Contains(status, s => s.Contains("đang làm lại (lần 2/", StringComparison.Ordinal));
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

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
            new InterviewScopeService(db, llm, prompts),
            new ScreenStepPlacementService(llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
    }

    // Trả lần lượt các lượt đã xếp sẵn cho purpose "BAChat" (lượt cuối lặp lại khi hết hàng đợi) và giữ lại
    // ngữ cảnh của từng lời gọi để test soi phần được nối thêm.
    private sealed class FakeLlm : ILlmClient
    {
        private readonly string _coverageMap;
        private readonly BAChatReply[] _replies;

        public FakeLlm(string coverageMap, params BAChatReply[] replies)
        {
            _coverageMap = coverageMap;
            _replies = replies;
        }

        /// <summary>Chuỗi mà fake đẩy qua onToken khi được truyền — để đo lượt nào thật sự stream.</summary>
        public string? EmitToken { get; init; }

        public int ChatCalls { get; private set; }

        public List<List<ChatMessage>> ChatMessages { get; } = new();

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(logContext.Purpose == "BARequirementCoverage"
                ? new LlmCallResult { IsSuccess = true, Content = _coverageMap }
                : new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            if (logContext.Purpose == "BARequirementCoverage")
                return Task.FromResult((ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).Result, (T?)null));
            if (logContext.Purpose != "BAChat")
                throw new InvalidOperationException($"Unexpected structured call: {logContext.Purpose}");

            // Chụp bản SAO: vòng đòi lại nối thêm vào chính list này, nên giữ tham chiếu là mọi lời gọi cùng
            // trỏ về một danh sách đã bị sửa.
            ChatMessages.Add(new List<ChatMessage>(messages));
            var reply = _replies[Math.Min(ChatCalls, _replies.Length - 1)];
            ChatCalls++;

            if (EmitToken != null)
                onToken?.Invoke(EmitToken);

            // Content là thứ vòng đòi lại chép lại dưới vai assistant — giữ nó giống đường thật (JSON của lượt).
            return Task.FromResult((
                new LlmCallResult { IsSuccess = true, Content = System.Text.Json.JsonSerializer.Serialize(reply) },
                (T?)(object)reply));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }
}
