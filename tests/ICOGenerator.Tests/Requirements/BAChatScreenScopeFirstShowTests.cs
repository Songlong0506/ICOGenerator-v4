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

// LẦN BÀY ĐẦU của bảng màn hình phải mang ĐỦ màn hình nghiệp vụ — BA thiết kế màn hình, không phải người dùng.
//
// Ca thật (dự án Locker): người dùng chốt bảng luồng 13 bước, rồi chốt bảng báo cáo. Bảng màn hình hiện ra
// ngay lượt sau với đúng bốn dòng — bốn màn hình `… Report` do bảng báo cáo gieo sang — và một dòng nhắc
// liệt kê cả 13 bước: "Mình chưa xếp được các bước này vào chức năng nào… anh/chị nhắn cho mình biết".
//
// Hai chỗ hỏng chồng lên nhau, mỗi test dưới đây giữ một chỗ:
//
//  1. Lượt chắt lọc phạm vi chạy ở HẬU KỲ và chờ các bảng đứng trước hết việc. Bảng báo cáo là bảng đứng
//     ngay trước: nó chốt xong thì đã gieo sẵn mục chờ duyệt, nên cổng bảng màn hình mở ngay lượt kế —
//     TRƯỚC khi lượt chắt lọc có cơ hội chạy lần nào. Bảng ra chỉ có màn hình báo cáo.
//  2. Lượt xếp chỗ bỏ cả lượt khi quá 8 bước mồ côi, tức đứng ngoài đúng ở ca cần nó nhất.
public class BAChatScreenScopeFirstShowTests : IDisposable
{
    private static readonly string ReadyForTheScreenTable = CoverageMapFixture.Map("""
        - ★ Mục tiêu / bài toán: [RÕ] Quản lý tủ locker.
        - ★ Đối tượng người dùng & vai trò: [RÕ] Nhân viên xin tủ, FCM cấp tủ.
        - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Xin tủ, duyệt, cấp tủ.
        - Quy trình hiện tại & điểm khó: [RÕ] Làm tay trên Excel.
        - Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] Tủ hư thì chuyển damaged.
        - Dữ liệu / danh mục chính: [RÕ] Tủ, khu, tòa.
        - Quy tắc nghiệp vụ & ràng buộc: [RÕ] Mỗi người một tủ.
        - Vòng đời & trạng thái: [RÕ] Available → In use → Damaged.
        - Thông báo / nhắc nhở: [RÕ] Báo FCM khi có request.
        - Báo cáo / thống kê: [RÕ] Báo cáo tình trạng tủ.
        - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
        - Quy mô sử dụng: [RÕ] Khoảng 2000 người.
        """);

    private const string StepRequest = "Vào ứng dụng request tủ cho chính mình";
    private const string StepApprove = "Approve yêu cầu của nhân viên";

    private const string ConfirmedReports = """
        [{"report":"Locker Status Report","question":"Bao nhiêu tủ ở từng trạng thái?","source":"Tủ","breakdown":"Theo khu","included":true}]
        """;

    // Đúng trạng thái ngay sau khi ReportMapDraftService gieo: mỗi báo cáo một dòng CHỜ DUYỆT.
    private const string ReportSeededScreens = """
        [{"screen":"Locker Status Report","purpose":"","functions":[],"covers":[],"included":true,"confirmedByUser":false}]
        """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _baId = Guid.NewGuid();

    public BAChatScreenScopeFirstShowTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(new Agent { Id = _baId, RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = _model.Id });
        db.SaveChanges();
    }

    private void SeedProject(string flowMap)
    {
        using var db = NewDb();
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Locker",
            Description = "quản lý tủ locker",
            RequirementCoverageMap = ReadyForTheScreenTable,
            FlowMap = flowMap,
            ReportMap = ConfirmedReports,
            ScreenScopeMap = ReportSeededScreens
        });
        db.SaveChanges();
    }

    private static string Flow(params string[] actions)
        => "[{\"name\":\"Xin và cấp tủ\",\"kind\":\"luồng chính\",\"role\":\"Nhân viên\",\"steps\":["
           + string.Join(",", actions.Select(a => $"{{\"actor\":\"Nhân viên\",\"action\":\"{a}\",\"outcome\":\"\",\"included\":true}}"))
           + "]}]";

    // CHỖ HỎNG 1. Lượt bảng báo cáo vừa được ráp là lượt chat đầu tiên mà lượt chắt lọc được phép chạy — và
    // cũng chính là lượt bảng màn hình hiện ra. Nó phải chạy TRƯỚC khi cổng chọn bảng, để màn hình nghiệp
    // vụ có mặt trong phạm vi mà BA dựng bảng lên.
    [Fact]
    public async Task OperationalScreens_AreHarvested_BeforeTheScreenTableIsFirstShown()
    {
        SeedProject(Flow(StepRequest, StepApprove));
        var llm = new FakeLlm
        {
            ScopeAdditions =
            {
                new ScopeAddition { Screen = "Locker Request", Purpose = "Nhân viên xin tủ.", Functions = { "Gửi yêu cầu xin tủ" } }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Mình đã rà bảng báo cáo.");

        Assert.Equal(1, llm.ScopeCalls);
        // Màn hình vừa chắt đã nằm trong danh sách phạm vi đưa cho lượt dựng bảng…
        Assert.Contains(llm.ChatSystemMessages, m => m.Contains("Locker Request", StringComparison.Ordinal));
        // …và trên chính bảng người dùng nhận được, cạnh màn hình báo cáo.
        Assert.Contains(result.ScreenScopeMap, r => r.Screen == "Locker Request");
        Assert.Contains(result.ScreenScopeMap, r => r.Screen == "Locker Status Report");
    }

    // CHỖ HỎNG 2. Bảng chỉ có màn hình báo cáo và MỌI bước đều mồ côi — nhiều hơn trần cũ (8). Đó là lúc
    // BA phải dựng nốt màn hình, không phải lúc trả nguyên danh sách bước về cho người dùng.
    [Fact]
    public async Task ManyOrphanSteps_AreStillPlacedByTheBA_InsteadOfHandedBackToTheUser()
    {
        var steps = Enumerable.Range(1, 10).Select(i => $"Bước nghiệp vụ số {i:00} của luồng cấp tủ").ToArray();
        SeedProject(Flow(steps));
        var llm = new FakeLlm
        {
            Placements = steps
                .Select((s, i) => new ScreenStepPlacement
                {
                    Step = s,
                    Screen = "Locker Assignment",
                    Function = $"Chức năng {i + 1}",
                    Purpose = "FCM cấp tủ cho nhân viên."
                })
                .ToList()
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Mình đã rà bảng báo cáo.");

        Assert.Empty(ScreenScopeMapBuilder.UncoveredActions(result.ScreenScopeMap, Flow(steps)));
        var added = result.ScreenScopeMap.Single(r => r.Screen == "Locker Assignment");
        Assert.Equal(steps.Length, added.Functions.Count);
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
            new ScreenStepPlacementService(llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Ba lời gọi có ý nghĩa: lượt chắt lọc phạm vi, lượt chat (trả bảng không gắn bước nào) và lượt xếp
    // chỗ. Mọi lời gọi phụ khác đều fail-open nên để hỏng hết.
    private sealed class FakeLlm : ILlmClient
    {
        public List<string> ChatSystemMessages = new();
        public List<ScopeAddition> ScopeAdditions = new();
        public List<ScreenStepPlacement> Placements = new();
        public int ScopeCalls;

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            var ok = new LlmCallResult { IsSuccess = true, Content = "{}" };

            if (logContext.Purpose == "BAInterviewScope")
            {
                ScopeCalls++;
                return Task.FromResult((ok, (T?)(object)new InterviewScope { ScopeAdditions = ScopeAdditions }));
            }

            if (logContext.Purpose == "BAChat")
            {
                ChatSystemMessages = messages
                    .Where(m => m.Role == ChatRole.System)
                    .Select(m => m.Text ?? string.Empty)
                    .ToList();

                // Model chỉ mô tả màn hình báo cáo và không gắn bước nào — phần còn lại do hệ thống bù.
                var reply = new BAChatReply
                {
                    Message = "Anh/chị rà bảng màn hình bên dưới giúp mình nhé.",
                    ScreenScopeMap = new List<ScreenScopeRow>
                    {
                        new()
                        {
                            Screen = "Locker Status Report",
                            Purpose = "Xem số tủ theo từng trạng thái.",
                            Functions = new List<ScreenFunction> { new() { Name = "Xem số tủ theo trạng thái" } }
                        }
                    }
                };
                return Task.FromResult((ok, (T?)(object)reply));
            }

            if (logContext.Purpose == "BAScreenStepPlacement")
                return Task.FromResult((ok, (T?)(object)new ScreenStepPlacementPlan { Placements = Placements }));

            return Task.FromResult((new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" }, (T?)null));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }
}
