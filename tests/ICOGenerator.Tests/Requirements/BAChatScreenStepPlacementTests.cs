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

// HAI SỢI DÂY GIỮ CHO BẢNG MÀN HÌNH KHÔNG HIỆN RA KÈM MỘT CÂU HỎI NGƯỢC NGƯỜI DÙNG.
//
// Luật ở tầng builder do ScreenStepPlacementTests giữ; ở đây đo phần NỐI vào lượt chat, vì cả hai sợi dây
// đều là loại hỏng câm — bảng vẫn hiện ra, nút gửi vẫn chạy, chỉ có điều phần việc lại rơi sang người dùng.
//
//  1. Danh sách BƯỚC LUỒNG đã chốt phải đi vào ngữ cảnh lượt bày bảng thành một bảng kê để ĐỐI CHIẾU. Các
//     bước ấy đã có sẵn trong khối "bảng luồng đã chốt", nhưng ở đó chúng là một câu chuyện kể theo từng
//     luồng — mà chỗ hỏng của lượt này chưa bao giờ là chỗ hiểu, nó là chỗ nối.
//  2. Còn bước nào lọt lưới thì BA phải TỰ XẾP CHỖ trước khi bảng hiện ra, và phải NÓI RA việc mình vừa làm.
public class BAChatScreenStepPlacementTests : IDisposable
{
    private const string StepViewStaff = "Xem danh sách nhân viên trực tiếp dưới quyền";
    private const string StepAssign = "Gán JD tương ứng cho từng nhân viên";
    private const string StepChecklistHeading = "### Các BƯỚC của bảng luồng đã chốt";

    // Cổng bảng màn hình mở LẠI được khi có màn hình mới lộ ra sau lúc chốt — đường ngắn nhất để lái một
    // lượt chat thật vào nhánh bày bảng (xem ScreenScopeDriftTests cho chính cơ chế đó).
    private static readonly string CoverageWithMainFlowClear = CoverageMapFixture.Map("""
        - ★ Mục tiêu / bài toán: [RÕ] Quản lý JD.
        - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Tạo, duyệt và gán JD.
        """);

    private const string ConfirmedFlow = """
        [{"name":"Tạo, duyệt và gán JD","kind":"luồng chính","role":"Manager orgUnit","steps":[
            {"actor":"Manager orgUnit","action":"Xem danh sách nhân viên trực tiếp dưới quyền","outcome":"","included":true},
            {"actor":"Manager orgUnit","action":"Gán JD tương ứng cho từng nhân viên","outcome":"JD được gán cho nhân viên","included":true}]}]
        """;

    // Bảng đã chốt ở lượt trước: có màn JD Assignment nhưng KHÔNG chức năng nào nhận bước "xem danh sách
    // nhân viên dưới quyền" — đúng trạng thái của ca thật JD Library 2.
    // Bảng đã chốt một dòng, cộng một màn hình vừa lộ ra sau đó (còn CHỜ DUYỆT) — đúng trạng thái mở lại
    // cổng bảng màn hình.
    private const string ConfirmedScreens = """
        [{"screen":"JD Assignment","purpose":"Gán JD cho từng nhân viên.",
          "functions":[{"name":"Tạo assignment","flowSteps":["Gán JD tương ứng cho từng nhân viên"],"included":true,"confirmedByUser":true}],
          "covers":[],"included":true,"confirmedByUser":true},
         {"screen":"JD Library","purpose":"","functions":[],"covers":[],"included":true,"confirmedByUser":false}]
        """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public BAChatScreenStepPlacementTests()
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
            Name = "JD Library",
            Description = "quản lý JD trong nhà máy",
            RequirementCoverageMap = CoverageWithMainFlowClear,
            FlowMap = ConfirmedFlow,
            ScreenScopeMap = ConfirmedScreens
        });
        db.SaveChanges();
    }

    // SỢI DÂY 1. Không có bảng kê này thì model chỉ có một câu chuyện để đọc, và bước lọt lưới là chuyện
    // thường — ca thật rơi đúng ở đây.
    [Fact]
    public async Task TheConfirmedFlowSteps_ReachTheScreenTableTurn_AsAChecklistToCover()
    {
        var llm = new FakeLlm();
        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "ok");

        var block = Assert.Single(llm.ChatSystemMessages, m => m.Contains(StepChecklistHeading, StringComparison.Ordinal));
        Assert.Contains(StepViewStaff, block);
        Assert.Contains(StepAssign, block);
    }

    // SỢI DÂY 2. Model trả về một bảng để sót bước ⇒ lượt xếp chỗ chạy, bảng đi ra đã KÍN, và dòng nhắc
    // dưới bảng không còn gì để hỏi.
    [Fact]
    public async Task AnOrphanStep_IsPlacedByTheBA_BeforeTheTableEverReachesTheUser()
    {
        var llm = new FakeLlm
        {
            Placement = new ScreenStepPlacement
            {
                Step = StepViewStaff,
                Screen = "JD Assignment",
                Function = "Xem danh sách nhân viên dưới quyền"
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "ok");

        var assignment = result.ScreenScopeMap.Single(r => r.Screen == "JD Assignment");
        Assert.Contains(assignment.Functions, f => f.Name == "Xem danh sách nhân viên dưới quyền");
        Assert.Empty(result.UncoveredFlowSteps);
        // …và việc vừa làm phải được nói ra: người dùng cần biết dòng mới ở đâu ra để rà đúng chỗ.
        Assert.Contains(StepViewStaff, result.Reply);
        Assert.Contains("Xem danh sách nhân viên dưới quyền", result.Reply);
    }

    // FAIL-OPEN. Lượt xếp chỗ hỏng (lời gọi lỗi, model trả rác) ⇒ bảng vẫn ra và dòng nhắc cũ vẫn nói thật.
    // Một lượt phụ không bao giờ được phép chặn bảng chính hiện ra.
    [Fact]
    public async Task TheTableStillShows_WithTheOldWarning_WhenThePlacementCallFails()
    {
        var llm = new FakeLlm { PlacementFails = true };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "ok");

        Assert.NotEmpty(result.ScreenScopeMap);
        Assert.Equal(new[] { StepViewStaff }, result.UncoveredFlowSteps);
        // Không xếp được gì thì KHÔNG được kể là đã xếp: câu đó sẽ đứng ngay trên dòng nhắc nói ngược lại.
        Assert.DoesNotContain("mình xếp", result.Reply);
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
            new ScreenStepPlacementService(llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Chỉ hai lời gọi có ý nghĩa ở đây: lượt chat (trả bảng còn sót bước) và lượt xếp chỗ. Mọi lời gọi phụ
    // khác đều fail-open nên để hỏng hết.
    private sealed class FakeLlm : ILlmClient
    {
        public List<string> ChatSystemMessages = new();
        public ScreenStepPlacement? Placement;
        public bool PlacementFails;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            if (logContext.Purpose == "BAChat")
            {
                ChatSystemMessages = messages
                    .Where(m => m.Role == ChatRole.System)
                    .Select(m => m.Text ?? string.Empty)
                    .ToList();

                // Bảng như model thật đã trả ở ca JD Library 2: đầy đủ màn hình, nhưng bước "xem danh sách
                // nhân viên dưới quyền" không chức năng nào nhận.
                var reply = new BAChatReply
                {
                    Message = "Anh/chị rà bảng màn hình bên dưới giúp mình nhé.",
                    ScreenScopeMap = new List<ScreenScopeRow>
                    {
                        new()
                        {
                            Screen = "JD Library",
                            Purpose = "Tra cứu và quản lý danh sách JD.",
                            Functions = new List<ScreenFunction> { new() { Name = "Xem danh sách JD" } }
                        }
                    }
                };
                return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)(object)reply));
            }

            if (logContext.Purpose == "BAScreenStepPlacement" && !PlacementFails)
            {
                var plan = new ScreenStepPlacementPlan();
                if (Placement != null)
                    plan.Placements.Add(Placement);
                return Task.FromResult((new LlmCallResult { IsSuccess = true, Content = "{}" }, (T?)(object)plan));
            }

            return Task.FromResult((new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" }, (T?)null));
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
