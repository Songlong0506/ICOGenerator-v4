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

// BA TỰ SOẠN hai bảng thiết kế — phạm vi màn hình và bảng phân quyền — thay vì bày chúng ra bắt người dùng
// chốt. Việc của người dùng là cung cấp yêu cầu (ai làm gì, khi nào, trạng thái nào); cần bao nhiêu màn
// hình, nút nào đặt ở màn nào, vai nào được CRUD gì là thiết kế. Ca gốc (dự án Locker): người dùng thêm
// trạng thái Need Clean sau khi bảng màn hình đã chốt, và BA hỏi ngược *"Nút «Thu hồi tủ» và nút «Đã
// clean» nên đặt ở màn hình nào?"* + ba chip tên màn hình.
//
// Bộ test giữ bốn thứ:
//  1. Lượt soạn phạm vi CHỐT cả bảng (cổng đóng ngay), đứng trên đủ mọi nguồn màn hình — kể cả màn hình
//     nghiệp vụ mà lượt chắt lọc vừa bắt ở đầu CHÍNH lượt đó.
//  2. Mối nối luồng ⇄ màn hình vẫn kín: bước mồ côi được BA xếp chỗ, bước ngoài phần mềm được ghi lên bảng
//     luồng — không câu nào hỏi ngược người dùng.
//  3. Phạm vi trôi sau lần chốt (một chức năng mới) được soạn nốt mà không đụng phần đã chốt.
//  4. Bảng phân quyền được soạn trên phạm vi ĐÃ CHỐT, nhóm của nó tích xanh NGAY ở lượt đó, và lượt chat
//     nói ra chỗ xem/sửa bảng.
public class BAChatDesignDraftTests : IDisposable
{
    private static readonly string EverythingElseClear = CoverageMapFixture.Map("""
        - ★ Mục tiêu / bài toán: [RÕ] Quản lý tủ locker.
        - ★ Đối tượng người dùng & vai trò: [RÕ] Nhân viên xin tủ, FCM cấp tủ.
        - ★ Chức năng & luồng nghiệp vụ chính: [RÕ] Xin tủ, duyệt, cấp tủ.
        - Quy trình hiện tại & điểm khó: [RÕ] Làm tay trên Excel.
        - Luồng ngoại lệ & trường hợp đặc biệt: [RÕ] Tủ hư thì chuyển damaged.
        - Dữ liệu / danh mục chính: [RÕ] Tủ, khu, tòa.
        - Quy tắc nghiệp vụ & ràng buộc: [RÕ] Mỗi người một tủ.
        - Vòng đời & trạng thái: [KHÔNG ÁP DỤNG]
        - Thông báo / nhắc nhở: [CHƯA HỎI]
        - Báo cáo / thống kê: [RÕ] Báo cáo tình trạng tủ.
        - Phân quyền theo nghiệp vụ: [CHƯA HỎI]
        - Quy mô sử dụng: [RÕ] Khoảng 2000 người.
        """);

    private const string StepRequest = "Vào ứng dụng request tủ cho chính mình";
    private const string StepAssign = "Chọn tủ Available để cấp cho người đó";
    private const string StepCall = "Gọi điện cho nhân viên nghỉ việc";

    private const string ConfirmedReports = """
        [{"report":"Locker Status Report","question":"Bao nhiêu tủ ở từng trạng thái?","source":"Tủ","breakdown":"Theo khu","ownScreen":true,"included":true}]
        """;

    // Đúng trạng thái ngay sau khi ReportMapDraftService gieo: mỗi báo cáo một dòng CHƯA CHỐT.
    private const string ReportSeededScreens = """
        [{"screen":"Locker Status Report","purpose":"","functions":[],"covers":[],"included":true,"confirmedByUser":false}]
        """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Agent _ba;

    public BAChatDesignDraftTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _ba = new Agent { Id = Guid.NewGuid(), RoleKey = AgentRoleKey.BusinessAnalyst, AiModelId = _model.Id };

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AiModels.Add(_model);
        db.Agents.Add(_ba);
        db.SaveChanges();
    }

    private void SeedProject(string flowMap, string? screens = ReportSeededScreens, string? coverage = null, string? reports = ConfirmedReports)
    {
        using var db = NewDb();
        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Locker",
            Description = "quản lý tủ locker",
            RequirementCoverageMap = coverage ?? EverythingElseClear,
            FlowMap = flowMap,
            ReportMap = reports,
            ScreenScopeMap = screens
        });
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId, AgentId = _ba.Id, Role = "user", Message = "Nhân viên vào app xin tủ, FCM cấp tủ."
        });
        db.SaveChanges();
    }

    private static string Flow(params string[] actions)
        => "[{\"name\":\"Xin và cấp tủ\",\"kind\":\"luồng chính\",\"role\":\"Nhân viên\",\"steps\":["
           + string.Join(",", actions.Select(a => $"{{\"actor\":\"FCM\",\"action\":\"{a}\",\"outcome\":\"\",\"included\":true}}"))
           + "]}]";

    // ── 1. Lượt soạn phạm vi ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScreenDraft_SettlesTheWholeTable_SoTheGateClosesAtOnce()
    {
        SeedProject(Flow(StepRequest));
        var llm = new FakeLlm
        {
            Screens =
            {
                new ScreenScopeRow
                {
                    Screen = "Locker Status Report",
                    Purpose = "Xem số tủ theo từng trạng thái.",
                    Functions = { new ScreenFunction { Name = "Xin tủ", FlowSteps = { StepRequest } } }
                }
            }
        };

        var project = await DraftScreensAsync(llm);

        var rows = ScreenScopeMapBuilder.Parse(project.ScreenScopeMap);
        Assert.All(rows, r => Assert.True(r.ConfirmedByUser));
        Assert.All(rows.SelectMany(r => r.Functions), f => Assert.True(f.ConfirmedByUser));
        Assert.False(ScreenScopeMapBuilder.HasPending(project.ScreenScopeMap));
        Assert.False(ScreenScopeGate.ShouldDraft(project));
        Assert.Equal("Xem số tủ theo từng trạng thái.", rows.Single().Purpose);
    }

    // Ô "phục vụ bước nào" trỏ vào bước luồng đã chốt — nên lượt soạn không được chạy khi bảng luồng còn
    // chờ người dùng gửi: gắn chức năng vào các bước chưa ai duyệt là gắn vào cát.
    [Fact]
    public async Task ScreenDraft_WaitsForTheFlowTable()
    {
        SeedProject(flowMap: null!);
        var llm = new FakeLlm();

        await DraftScreensAsync(llm);

        Assert.Equal(0, llm.ScreenDraftCalls);
    }

    // Lời gọi lỗi ⇒ không ghi gì, cổng còn mở, lượt kế soạn lại. Một bảng chốt nửa vời tệ hơn không chốt.
    [Fact]
    public async Task ScreenDraft_WritesNothing_WhenTheCallFails()
    {
        SeedProject(Flow(StepRequest));
        var llm = new FakeLlm { ScreenDraftFails = true };

        var project = await DraftScreensAsync(llm);

        Assert.True(ScreenScopeMapBuilder.HasPending(project.ScreenScopeMap));
    }

    // ── 2. Mối nối luồng ⇄ màn hình ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScreenDraft_GetsTheConfirmedFlowSteps_AsAChecklistToCover()
    {
        SeedProject(Flow(StepRequest, StepAssign));
        var llm = new FakeLlm();

        await DraftScreensAsync(llm);

        Assert.Contains(StepRequest, llm.ScreenDraftInput);
        Assert.Contains(StepAssign, llm.ScreenDraftInput);
        Assert.Contains("- Locker Status Report", llm.ScreenDraftInput);
    }

    [Fact]
    public async Task AnOrphanStep_IsPlacedByTheBA_BeforeTheScopeIsSettled()
    {
        SeedProject(Flow(StepAssign));
        var llm = new FakeLlm
        {
            Placements = { new ScreenStepPlacement { Step = StepAssign, Screen = "Locker Assignment", Function = "Cấp tủ", Purpose = "FCM cấp tủ." } }
        };

        var project = await DraftScreensAsync(llm);

        var rows = ScreenScopeMapBuilder.Parse(project.ScreenScopeMap);
        Assert.Contains(rows, r => r.Screen == "Locker Assignment" && r.ConfirmedByUser
            && r.Functions.Any(f => f.Name == "Cấp tủ" && f.ConfirmedByUser));
        Assert.Empty(ScreenScopeMapBuilder.UncoveredActions(rows, project.FlowMap));
    }

    [Fact]
    public async Task AStepDoneOutsideTheSoftware_IsRecordedOnTheFlow()
    {
        const string reason = "FCM gọi điện thoại, ứng dụng chỉ ghi nhận kết quả";
        SeedProject(Flow(StepCall));
        var llm = new FakeLlm { Outside = { new ScreenStepOutside { Step = StepCall, Reason = reason } } };

        var project = await DraftScreensAsync(llm);

        Assert.Contains($"[NGOÀI PHẦN MỀM — {reason}]", FlowMapBuilder.RenderConfirmedBlock(project.FlowMap));
        Assert.Empty(ScreenScopeMapBuilder.UncoveredActions(ScreenScopeMapBuilder.Parse(project.ScreenScopeMap), project.FlowMap));
    }

    // ── 3. Phạm vi trôi sau lần chốt ────────────────────────────────────────────────────────────────

    // Ca gốc: người dùng thêm "FCM bấm Đã clean" sau khi phạm vi đã chốt. Lượt chắt lọc ghép chức năng mới
    // vào màn đang giữ tủ (CHƯA CHỐT) ⇒ cổng mở lại ⇒ lượt soạn chỉ gắn bước cho chức năng mới, giữ nguyên
    // mọi thứ đã chốt, rồi chốt nốt.
    [Fact]
    public async Task ANewFunctionAfterSettling_IsDraftedWithoutTouchingTheSettledPart()
    {
        const string stepClean = "FCM bấm Đã clean sau khi dọn tủ";
        const string settled = """
            [{"screen":"Locker List","purpose":"Danh sách tủ theo khu.","functions":[
                {"name":"Xem danh sách tủ","flowSteps":[],"included":true,"confirmedByUser":true},
                {"name":"Xác nhận đã clean","flowSteps":[],"included":true,"confirmedByUser":false}],
              "covers":[],"included":true,"confirmedByUser":true}]
            """;
        SeedProject(Flow(stepClean), screens: settled);
        var llm = new FakeLlm
        {
            Screens =
            {
                new ScreenScopeRow
                {
                    Screen = "Locker List",
                    Purpose = "Câu model đoán lại — không được đè lên câu đã chốt.",
                    Functions = { new ScreenFunction { Name = "Xác nhận đã clean", FlowSteps = { stepClean } } }
                }
            },
            Placements = { new ScreenStepPlacement { Step = stepClean, Screen = "Locker List", Function = "Xác nhận đã clean" } }
        };

        var project = await DraftScreensAsync(llm);

        Assert.Contains("Chức năng MỚI", llm.ScreenDraftInput);
        Assert.Contains("Xác nhận đã clean (ở Locker List)", llm.ScreenDraftInput);

        var row = ScreenScopeMapBuilder.Parse(project.ScreenScopeMap).Single();
        Assert.Equal("Danh sách tủ theo khu.", row.Purpose);
        Assert.Contains(row.Functions, f => f.Name == "Xác nhận đã clean" && f.ConfirmedByUser && f.FlowSteps.Contains(stepClean));
        Assert.False(ScreenScopeMapBuilder.HasPending(project.ScreenScopeMap));
    }

    // ── 4. Bảng phân quyền ──────────────────────────────────────────────────────────────────────────

    // Soạn quyền trên một phạm vi còn đang trôi là thiếu đúng các chức năng sắp được thêm.
    [Fact]
    public void PermissionDraft_WaitsForTheScreenScopeToSettle()
    {
        var project = new Project
        {
            RequirementCoverageMap = EverythingElseClear,
            ScreenScopeMap = ReportSeededScreens
        };

        Assert.False(PermissionMatrixGate.ShouldDraft(project));

        var settled = ScreenScopeMapBuilder.Parse(ReportSeededScreens);
        settled.ForEach(r => r.ConfirmedByUser = true);
        project.ScreenScopeMap = System.Text.Json.JsonSerializer.Serialize(settled);
        Assert.True(PermissionMatrixGate.ShouldDraft(project));
    }

    // MỘT lượt chat: lượt chắt lọc bắt màn hình nghiệp vụ → lượt soạn phạm vi chốt nó cùng màn hình báo cáo
    // → lượt soạn quyền đứng trên đúng phạm vi đó → nhóm «Phân quyền» tích xanh ngay → lượt chat nói ra chỗ
    // xem/sửa bảng. Không bảng nào hiện ra trong khung chat.
    [Fact]
    public async Task OneChatTurn_DraftsScopeThenPermissions_AndSealsThePermissionGroup()
    {
        SeedProject(Flow(StepRequest));
        var llm = new FakeLlm
        {
            ScopeAdditions = { new ScopeAddition { Screen = "Locker Request", Purpose = "Nhân viên xin tủ.", Functions = { "Gửi yêu cầu xin tủ" } } },
            Screens =
            {
                new ScreenScopeRow
                {
                    Screen = "Locker Request",
                    Purpose = "Nhân viên xin tủ.",
                    Functions = { new ScreenFunction { Name = "Gửi yêu cầu xin tủ", FlowSteps = { StepRequest } } }
                }
            },
            PermissionRows =
            {
                new PermissionMatrixRow
                {
                    Screen = "Locker Request",
                    Function = "Gửi yêu cầu xin tủ",
                    Grants = { new PermissionGrant { Role = "Nhân viên", Scope = PermissionScope.Own } }
                }
            }
        };

        await using var db = NewDb();
        var result = await NewSut(db, llm).ChatAsync(_projectId, "Nhân viên tự vào app xin tủ.");

        // Màn hình vừa chắt có mặt ngay ở lượt soạn đầu, cạnh màn hình báo cáo.
        Assert.Contains("Locker Request", llm.ScreenDraftInput);
        Assert.Contains("Locker Status Report", llm.ScreenDraftInput);
        // Lượt soạn quyền đọc đúng phạm vi đã chốt.
        Assert.Contains("chức năng: Gửi yêu cầu xin tủ", llm.PermissionDraftInput);

        await using var fresh = NewDb();
        var project = await fresh.Projects.SingleAsync(p => p.Id == _projectId);
        Assert.False(ScreenScopeMapBuilder.HasPending(project.ScreenScopeMap));
        Assert.True(PermissionMatrixGate.IsConfirmed(project.PermissionMatrix));

        // Tích xanh ngay ở lượt này — panel bấm được để mở popup, cổng readiness có dòng đó để mở nút.
        var permissions = result.Coverage.Single(x => x.Key == CoverageGroupKey.Permissions);
        Assert.Equal(CoverageStatus.Clear, permissions.Status);
        Assert.Contains(CoverageConfirmedTableGuard.PermissionEvidence, permissions.Known);

        // Lượt chat nói ra chỗ xem/sửa, và ngữ cảnh của nó đã mang bảng thay cho lệnh "để cuối".
        Assert.EndsWith(BAChatService.PermissionMatrixDraftedNotice.Trim(), result.Reply.Trim());
        Assert.Contains(llm.ChatSystemMessages, m => m.StartsWith("## Bảng phân quyền của ứng dụng", StringComparison.Ordinal));
        Assert.DoesNotContain(llm.ChatSystemMessages, m => m.StartsWith("## Nhóm «Phân quyền theo nghiệp vụ» — ĐỂ CUỐI", StringComparison.Ordinal));
    }

    private async Task<Project> DraftScreensAsync(FakeLlm llm)
    {
        await using (var db = NewDb())
        {
            var project = await db.Projects.SingleAsync(p => p.Id == _projectId);
            var prompts = new StubPrompts();
            await new ScreenScopeDraftService(db, llm, prompts, new ScreenStepPlacementService(llm, prompts))
                .UpdateAsync(project, _ba, _model);
        }

        await using var fresh = NewDb();
        return await fresh.Projects.SingleAsync(p => p.Id == _projectId);
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
            new ScreenScopeDraftService(db, llm, prompts, new ScreenStepPlacementService(llm, prompts)),
            new PermissionMatrixDraftService(db, llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Các lời gọi có ý nghĩa: chắt lọc phạm vi, soạn phạm vi, xếp chỗ, soạn quyền và lượt chat. Mọi lời gọi
    // phụ khác (bản đồ bao phủ, bộ nhớ…) đều fail-open nên để hỏng hết.
    private sealed class FakeLlm : ILlmClient
    {
        public List<ScopeAddition> ScopeAdditions = new();
        public List<ScreenScopeRow> Screens = new();
        public List<ScreenStepPlacement> Placements = new();
        public List<ScreenStepOutside> Outside = new();
        public List<PermissionMatrixRow> PermissionRows = new();
        public bool ScreenDraftFails;

        public int ScreenDraftCalls;
        public string ScreenDraftInput = "";
        public string PermissionDraftInput = "";
        public List<string> ChatSystemMessages = new();

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            var ok = new LlmCallResult { IsSuccess = true, Content = "{}" };
            var input = string.Join("\n", messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text ?? string.Empty));

            switch (logContext.Purpose)
            {
                case "BAInterviewScope":
                    return Done(ok, new InterviewScope { ScopeAdditions = ScopeAdditions });

                case "BAScreenScopeDraft":
                    ScreenDraftCalls++;
                    ScreenDraftInput = input;
                    return ScreenDraftFails
                        ? Task.FromResult((new LlmCallResult { IsSuccess = false, ErrorMessage = "boom" }, (T?)null))
                        : Done(ok, new ScreenScopeDraft { Screens = Screens });

                case "BAScreenStepPlacement":
                    return Done(ok, new ScreenStepPlacementPlan { Placements = Placements, OutsideSoftware = Outside });

                case "BAPermissionMatrixDraft":
                    PermissionDraftInput = input;
                    return Done(ok, new PermissionMatrixDraft { Rows = PermissionRows });

                case "BAChat":
                    ChatSystemMessages = messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text ?? string.Empty).ToList();
                    return Done(ok, new BAChatReply
                    {
                        Message = "Mỗi người được giữ tối đa mấy tủ?",
                        Suggestions = new List<string> { "Một tủ", "Hai tủ" }
                    });
            }

            return Task.FromResult((new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" }, (T?)null));

            static Task<(LlmCallResult, T?)> Done(LlmCallResult result, object value) => Task.FromResult((result, (T?)value));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }
}
