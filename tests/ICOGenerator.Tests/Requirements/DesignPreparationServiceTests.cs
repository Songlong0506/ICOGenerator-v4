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

// BA TỰ THIẾT KẾ — danh mục tác vụ, bản thiết kế màn hình (UiUx), bảng phân quyền — ở bước "Write Requirement"
// (DesignPreparationService), không trong buổi phỏng vấn. Việc của người dùng là cung cấp yêu cầu (ai làm gì,
// khi nào, trạng thái nào); cần bao nhiêu màn hình, nút nào đặt ở màn nào, vai nào được làm gì là thiết kế. Ca
// gốc (dự án Locker): người dùng thêm trạng thái Need Clean sau khi bảng màn hình đã chốt, và BA hỏi ngược
// *"Nút «Thu hồi tủ» và nút «Đã clean» nên đặt ở màn hình nào?"*.
//
// Bộ test giữ sáu thứ:
//  1. Lượt thiết kế ghi một bản ĐỦ (mọi tác vụ có chỗ); lỗi thì không ghi gì và báo Failed.
//  2. Mối nối luồng ⇄ tác vụ kín: bước mồ côi được hỏi lại rồi có lưới an toàn; bước ngoài phần mềm được ghi
//     lên bảng luồng.
//  3. Vòng phản biện: lỗi lint đi ngược lại model, bản ÍT lỗi nhất được giữ.
//  4. Bấm lại nút: không lượt mới ⇒ không gọi model; lượt mới không thêm việc ⇒ chỉ một lời gọi danh mục;
//     thêm việc ⇒ thiết kế lại, và sau khi Brief đã duyệt thì chỉ PATCH.
//  5. Bảng phân quyền khoá theo ID tác vụ: soạn ngay sau bản thiết kế, tác vụ mới được bổ sung dòng mà không
//     đụng ô người dùng đã sửa, và thiết kế lại màn không làm mất quyền.
//  6. Luật lỗi của bước: thiết kế lỗi ⇒ dừng (không soạn Brief); phân quyền lỗi ⇒ đi tiếp. Và lượt chat KHÔNG
//     còn thiết kế gì.
public class DesignPreparationServiceTests : IDisposable
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
        - Quy mô sử dụng: [RÕ] Khoảng 2000 người.
        """);

    private const string StepRequest = "Nhân viên gửi ticket xin tủ";
    private const string StepAssign = "FCM cấp tủ cho nhân viên";
    private const string StepCall = "Gọi điện cho nhân viên nghỉ việc";

    private const string ConfirmedReports = """
        [{"report":"Tủ trống theo khu","question":"còn bao nhiêu tủ trống","source":"Tủ","breakdown":"Theo khu","ownScreen":false,"screen":"Locker Dashboard","included":true}]
        """;

    private const string ReportTaskName = "Tủ trống theo khu — còn bao nhiêu tủ trống";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AiModel _model = new() { Id = Guid.NewGuid(), ModelId = "test" };
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Agent _ba;

    public DesignPreparationServiceTests()
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

    private void SeedProject(string? flowMap, string? blueprint = null, string? coverage = null, string? reports = ConfirmedReports,
        string? permissionMatrix = null, int briefApprovedTurns = 0)
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
            UxBlueprint = blueprint,
            PermissionMatrix = permissionMatrix,
            BriefApprovedTurnCount = briefApprovedTurns
        });
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId, AgentId = _ba.Id, Role = "user", Message = "Nhân viên vào app xin tủ, FCM cấp tủ."
        });
        db.SaveChanges();
    }

    private static string Flow(params (string Actor, string Action, string Outcome)[] steps)
        => "[{\"name\":\"Xin và cấp tủ\",\"kind\":\"main\",\"role\":\"Nhân viên\",\"steps\":["
           + string.Join(",", steps.Select(s => $"{{\"actor\":\"{s.Actor}\",\"action\":\"{s.Action}\",\"outcome\":\"{s.Outcome}\",\"included\":true}}"))
           + "]}]";

    private static string RequestAndAssign() => Flow(("Nhân viên", StepRequest, "Pending"), ("FCM", StepAssign, "Done"));

    // Danh mục khớp RequestAndAssign: hai tác vụ trên phiếu xin tủ + một tác vụ xem.
    private static UxInventoryDraft LockerInventory() => new()
    {
        Objects =
        {
            new UxObject { Name = "Locker Ticket", States = { "Pending", "Done" }, FieldCount = 3 },
            new UxObject { Name = "Locker", States = { "Available", "Assigned" }, FieldCount = 4 }
        },
        Tasks =
        {
            new UxTaskDraft { Name = "Gửi ticket xin tủ", Object = "Locker Ticket", Kind = "create", Actors = { "Nhân viên" }, FlowSteps = { StepRequest }, Source = "flow" },
            new UxTaskDraft { Name = "Cấp tủ cho nhân viên", Object = "Locker Ticket", Kind = "transition", Actors = { "FCM" }, FlowSteps = { StepAssign }, FromState = "Pending", ToState = "Done", Source = "flow" },
            new UxTaskDraft { Name = "Xem ticket", Object = "Locker Ticket", Kind = "browse", Actors = { "Nhân viên", "FCM" } }
        }
    };

    // Một bản thiết kế TỐT cho LockerInventory: một nhà cho phiếu, dashboard cho báo cáo.
    private static UxBlueprintDraft GoodDesign() => new()
    {
        Screens =
        {
            new UxScreenDraft
            {
                Name = "Locker Dashboard", Pattern = "dashboard", Purpose = "Tổng quan tủ", Audience = { "FCM" },
                Actions = { new UxAction { Task = ReportTaskName, Label = "Tủ trống theo khu", Placement = "section" } }
            },
            new UxScreenDraft
            {
                Name = "Locker Tickets", Pattern = "list", Object = "Locker Ticket", Purpose = "Mọi ticket xin tủ", Audience = { "Nhân viên", "FCM" },
                Actions =
                {
                    new UxAction { Task = "T-03", Label = "Tìm ticket", Placement = "filter" },
                    new UxAction { Task = "T-01", Label = "Gửi ticket", Placement = "toolbar", Prominence = "primary", Form = "modal" },
                    new UxAction { Task = "T-02", Label = "Cấp tủ", Placement = "row", Prominence = "primary" }
                }
            }
        },
        Navigation =
        {
            new UxNavigation { Role = "FCM", Landing = "Locker Dashboard", Items = { new UxNavItem { Label = "Locker Dashboard", Screen = "Locker Dashboard" }, new UxNavItem { Label = "Locker Tickets", Screen = "Locker Tickets" } } },
            new UxNavigation { Role = "Nhân viên", Landing = "Locker Tickets", Items = { new UxNavItem { Label = "Locker Tickets", Screen = "Locker Tickets" } } }
        }
    };

    // ── 1. Lượt thiết kế ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Design_WritesACompleteBlueprint_SoItIsNotStaleAfterwards()
    {
        SeedProject(RequestAndAssign());
        var llm = new FakeLlm { Inventory = LockerInventory(), Design = GoodDesign() };

        var (outcome, project) = await PrepareDesignAsync(llm);

        Assert.Equal(UxDesignOutcome.Designed, outcome);

        var bp = UxBlueprintBuilder.Parse(project.UxBlueprint);
        Assert.Equal(1, bp.Version);
        Assert.Equal(new[] { "T-01", "T-02", "T-03", "T-04" }, bp.Tasks.Select(t => t.Id));
        Assert.All(bp.Tasks, t => Assert.True(t.Placed));
        Assert.Contains(bp.Tasks, t => t.Kind == UxTaskKind.Report && t.Name == ReportTaskName);
        Assert.Empty(UxBlueprintLint.Errors(bp.Findings));
        Assert.False(UxDesignGate.IsStale(project, turnCount: 1));
        Assert.Equal(1, project.UxDesignedTurnCount);
        Assert.Equal(0, bp.Metrics.AvgScreenSwitchesPerFlow);
        Assert.Equal(0, llm.RevisionCalls);
    }

    // Lời gọi lỗi ⇒ không ghi gì và báo Failed — caller (DesignPreparationService) dừng vòng soạn Brief.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Design_WritesNothing_WhenACallFails(bool inventoryFails, bool designFails)
    {
        SeedProject(RequestAndAssign());
        var llm = new FakeLlm { Inventory = LockerInventory(), Design = GoodDesign(), InventoryFails = inventoryFails, DesignFails = designFails };

        var (outcome, project) = await PrepareDesignAsync(llm);

        Assert.Equal(UxDesignOutcome.Failed, outcome);
        Assert.Null(project.UxBlueprint);
        Assert.True(UxDesignGate.IsStale(project, turnCount: 1));
    }

    // ── 2. Mối nối luồng ⇄ tác vụ ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Inventory_GetsTheConfirmedFlowSteps_AsAChecklistToCover()
    {
        SeedProject(RequestAndAssign());
        var llm = new FakeLlm { Inventory = LockerInventory(), Design = GoodDesign() };

        await PrepareDesignAsync(llm);

        Assert.Contains($"bước: {StepRequest}", llm.InventoryInput);
        Assert.Contains($"bước: {StepAssign}", llm.InventoryInput);
        Assert.Contains("TỰ gieo mỗi báo cáo", llm.InventoryInput);
        // Thiết kế nhận đầu vào JSON máy đọc được — cùng định dạng mà Prompt Evals đọc ngược.
        Assert.NotNull(UxDesignInputBuilder.TryParse(llm.DesignInput));
    }

    [Fact]
    public async Task AnOrphanStep_IsAskedAgain_ThenCoveredBeforeTheBlueprintIsSaved()
    {
        SeedProject(RequestAndAssign());
        var inventory = LockerInventory();
        inventory.Tasks.RemoveAt(1); // model quên bước cấp tủ
        var llm = new FakeLlm
        {
            Inventory = inventory,
            OrphanAnswer = new UxInventoryDraft
            {
                Tasks = { new UxTaskDraft { Name = "Cấp tủ", Object = "Locker Ticket", Kind = "transition", Actors = { "FCM" }, FlowSteps = { StepAssign } } }
            },
            Design = GoodDesign()
        };

        var (_, project) = await PrepareDesignAsync(llm);

        Assert.Contains(StepAssign, llm.OrphanDemand);
        var bp = UxBlueprintBuilder.Parse(project.UxBlueprint);
        Assert.Empty(UxInventoryBuilder.UncoveredSteps(bp.Tasks, project.FlowMap));
        Assert.All(bp.Tasks, t => Assert.True(t.Placed));
    }

    // Model vẫn bỏ sót sau lần hỏi lại ⇒ lưới an toàn dựng một tác vụ đúng bằng bước đó, và vòng vá đặt nó.
    [Fact]
    public async Task AStillOrphanStep_GetsAFallbackTask_SoNoConfirmedStepFallsOutOfTheApp()
    {
        SeedProject(RequestAndAssign());
        var inventory = LockerInventory();
        inventory.Tasks.RemoveAt(1);
        var llm = new FakeLlm { Inventory = inventory, OrphanAnswer = new UxInventoryDraft(), Design = GoodDesign() };

        var (_, project) = await PrepareDesignAsync(llm);

        var bp = UxBlueprintBuilder.Parse(project.UxBlueprint);
        var fallback = Assert.Single(bp.Tasks, t => t.FlowSteps.Contains(StepAssign));
        Assert.Equal(new[] { "FCM" }, fallback.Actors);
        Assert.NotNull(UxBlueprintBuilder.HomeOf(bp, fallback.Id));
    }

    [Fact]
    public async Task AStepDoneOutsideTheSoftware_IsRecordedOnTheFlow()
    {
        const string reason = "FCM gọi điện thoại, ứng dụng chỉ ghi nhận kết quả";
        SeedProject(Flow(("Nhân viên", StepRequest, "Pending"), ("FCM", StepCall, "")));
        var inventory = LockerInventory();
        inventory.Tasks.RemoveAt(1);
        inventory.OutsideSoftware.Add(new UxOutsideStep { Step = StepCall, Reason = reason });
        var llm = new FakeLlm { Inventory = inventory, Design = GoodDesign() };

        var (_, project) = await PrepareDesignAsync(llm);

        Assert.Contains($"[NGOÀI PHẦN MỀM — {reason}]", FlowMapBuilder.RenderConfirmedBlock(project.FlowMap));
        Assert.Equal(0, llm.OrphanCalls);
        Assert.Empty(UxInventoryBuilder.UncoveredSteps(UxBlueprintBuilder.Parse(project.UxBlueprint).Tasks, project.FlowMap));
    }

    // ── 3. Vòng phản biện ───────────────────────────────────────────────────────────────────────────

    // "My Tickets" + "Locker Tickets" cho cùng một đối tượng là lỗi duplicate-home: model nhận lại đúng mã lỗi
    // và sửa; bản sửa (ít lỗi hơn) được giữ.
    [Fact]
    public async Task LintErrors_GoBackToTheModel_AndTheBetterRevisionIsKept()
    {
        SeedProject(RequestAndAssign());
        var llm = new FakeLlm { Inventory = LockerInventory(), Design = WithMyTickets(), Revision = GoodDesign() };

        var (_, project) = await PrepareDesignAsync(llm);

        Assert.Equal(1, llm.RevisionCalls);
        Assert.Contains(UxLintCode.DuplicateHome, llm.RevisionDemand);
        Assert.DoesNotContain(UxBlueprintBuilder.Parse(project.UxBlueprint).Screens, s => s.Name == "My Tickets");
    }

    // Vòng sửa làm hỏng thêm thì bản đầu (ít lỗi hơn) thắng.
    [Fact]
    public async Task AWorseRevision_NeverReplacesTheBetterDraft()
    {
        SeedProject(RequestAndAssign());
        var llm = new FakeLlm { Inventory = LockerInventory(), Design = WithMyTickets(), Revision = new UxBlueprintDraft() };

        var (_, project) = await PrepareDesignAsync(llm);

        Assert.Equal(UxDesignService.MaxRevisions, llm.RevisionCalls);
        Assert.Contains(UxBlueprintBuilder.Parse(project.UxBlueprint).Screens, s => s.Name == "My Tickets");
    }

    private static UxBlueprintDraft WithMyTickets()
    {
        var bad = GoodDesign();
        bad.Screens.Add(new UxScreenDraft
        {
            Name = "My Tickets", Pattern = "list", Object = "Locker Ticket", Audience = { "Nhân viên" },
            Actions = { new UxAction { Task = "T-03", Label = "Ticket của tôi", Placement = "filter" } }
        });
        return bad;
    }

    // ── 4. Bấm lại nút ──────────────────────────────────────────────────────────────────────────────

    // Không có lượt hội thoại mới và các bảng không đổi ⇒ bản đang lưu vẫn đúng: không gọi model lần nào.
    [Fact]
    public async Task PressingAgain_WithoutNewTurns_CallsNoModel()
    {
        SeedProject(RequestAndAssign());
        await PrepareDesignAsync(new FakeLlm { Inventory = LockerInventory(), Design = GoodDesign() });

        var llm = new FakeLlm();
        var (outcome, _) = await PrepareDesignAsync(llm);

        Assert.Equal(UxDesignOutcome.Unchanged, outcome);
        Assert.Equal(0, llm.InventoryCalls);
        Assert.Equal("", llm.DesignInput);
    }

    // Lượt mới mà danh mục không thêm việc nào ⇒ dừng sau MỘT lời gọi danh mục, bản thiết kế giữ nguyên, con
    // trỏ tiến tới cuối hội thoại (bấm lần sau không gọi lại).
    [Fact]
    public async Task ANewTurnWithoutNewWork_StopsAfterTheInventoryCall()
    {
        SeedProject(RequestAndAssign());
        var first = (await PrepareDesignAsync(new FakeLlm { Inventory = LockerInventory(), Design = GoodDesign() })).Project;
        AddUserTurn("Đúng rồi, như vậy là đủ.");

        var llm = new FakeLlm { Inventory = LockerInventory() };
        var (outcome, project) = await PrepareDesignAsync(llm);

        Assert.Equal(UxDesignOutcome.Unchanged, outcome);
        Assert.Equal(1, llm.InventoryCalls);
        Assert.Equal("", llm.DesignInput);
        Assert.Equal(first.UxBlueprint, project.UxBlueprint);
        Assert.Equal(2, project.UxDesignedTurnCount);
    }

    // Lượt mới kể thêm một việc ⇒ danh mục thêm tác vụ chưa xếp chỗ ⇒ thiết kế lại. Brief đã duyệt ⇒ PATCH:
    // giữ cấu trúc, chỉ xếp chỗ phần mới.
    [Fact]
    public async Task ANewTask_IsDesignedIn_AndAfterBriefApprovalTheDesignOnlyPatches()
    {
        SeedProject(RequestAndAssign());
        var designed = (await PrepareDesignAsync(new FakeLlm { Inventory = LockerInventory(), Design = GoodDesign() })).Project;
        var bp = UxBlueprintBuilder.Parse(designed.UxBlueprint);
        AddUserTurn("Nhân viên cũng huỷ được ticket khi chưa được cấp tủ.");
        await using (var db = NewDb())
        {
            var p = await db.Projects.SingleAsync(x => x.Id == _projectId);
            p.BriefApprovedTurnCount = 1;
            await db.SaveChangesAsync();
            Assert.Equal(UxDesignMode.Patch, UxDesignGate.Mode(p));
        }

        var inventory = LockerInventory();
        inventory.Tasks.Add(new UxTaskDraft { Name = "Huỷ ticket", Object = "Locker Ticket", Kind = "delete", Actors = { "Nhân viên" } });
        var patched = GoodDesign();
        patched.Screens[1].Actions.Add(new UxAction { Task = "T-05", Label = "Huỷ ticket", Placement = "row", Prominence = "overflow" });
        var llm = new FakeLlm { Inventory = inventory, Design = patched };

        var (outcome, project) = await PrepareDesignAsync(llm);

        Assert.Equal(UxDesignOutcome.Designed, outcome);
        Assert.Contains("Chế độ: PATCH", llm.DesignInput);
        var after = UxBlueprintBuilder.Parse(project.UxBlueprint);
        Assert.Equal(2, after.Version);
        Assert.True(after.Tasks.Single(t => t.Id == "T-05").Placed);
        Assert.Equal(bp.Screens.Select(s => s.Id), after.Screens.Select(s => s.Id)); // màn giữ nguyên ID
        Assert.False(UxDesignGate.IsStale(project, turnCount: 2));
    }

    // ── 5. Bảng phân quyền ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PermissionDraft_WaitsForPendingTasksToBePlaced()
    {
        var bp = new UxBlueprint
        {
            Tasks = { new UxTask { Id = "T-01", Name = "Gửi ticket", Placed = true }, new UxTask { Id = "T-02", Name = "Huỷ ticket", Placed = false } },
            Screens = { new UxScreen { Id = "S-01", Name = "Locker Tickets", Actions = { new UxAction { Task = "T-01", Label = "Gửi" } } } }
        };
        var project = new Project { UxBlueprint = UxBlueprintBuilder.Serialize(bp) };

        Assert.False(PermissionMatrixGate.ShouldDraft(project));

        bp.Tasks[1].Placed = true;
        project.UxBlueprint = UxBlueprintBuilder.Serialize(bp);
        Assert.True(PermissionMatrixGate.ShouldDraft(project));
    }

    // MỘT lần bấm: thiết kế → soạn quyền trên đúng các tác vụ đó, theo thứ tự màn hình.
    [Fact]
    public async Task Prepare_DesignsThenDraftsPermissionsOnThoseTasks()
    {
        SeedProject(RequestAndAssign());
        var llm = new FakeLlm
        {
            Inventory = LockerInventory(),
            Design = GoodDesign(),
            PermissionRows =
            {
                new PermissionMatrixRow { TaskId = "T-01", Grants = { new PermissionGrant { Role = "Nhân viên", Scope = PermissionScope.Own } } },
                new PermissionMatrixRow { TaskId = "T-02", Grants = { new PermissionGrant { Role = "FCM", Scope = PermissionScope.All } } }
            }
        };

        var (ok, project) = await PrepareAllAsync(llm);

        Assert.True(ok);
        Assert.Contains("T-01 | Locker Tickets | Gửi ticket xin tủ", llm.PermissionDraftInput);
        var rows = PermissionMatrixBuilder.Parse(project.PermissionMatrix);
        Assert.Equal(new[] { "T-04", "T-03", "T-01", "T-02" }, rows.Select(r => r.TaskId)); // theo thứ tự màn hình
        Assert.Equal("Locker Tickets", rows.Single(r => r.TaskId == "T-01").Screen);
    }

    // ── 6. Luật lỗi của bước, và lượt chat không còn thiết kế ──────────────────────────────────────

    // Thiết kế lỗi ⇒ fail-closed: Brief đọc "Các màn hình chính" từ bản thiết kế, soạn khi không có nó là soạn
    // thiếu màn hình. Bảng phân quyền cũng không được soạn (không có tác vụ nào để làm dòng).
    [Fact]
    public async Task Prepare_Fails_WhenTheDesignFails()
    {
        SeedProject(RequestAndAssign());
        var llm = new FakeLlm { Inventory = LockerInventory(), Design = GoodDesign(), DesignFails = true };

        var (ok, project) = await PrepareAllAsync(llm);

        Assert.False(ok);
        Assert.Null(project.UxBlueprint);
        Assert.Equal("", llm.PermissionDraftInput);
    }

    // Phân quyền lỗi ⇒ fail-open: Brief không đọc bảng phân quyền, nên vòng soạn đi tiếp; lần bấm sau soạn bù.
    [Fact]
    public async Task Prepare_GoesOn_WhenOnlyThePermissionDraftFails()
    {
        SeedProject(RequestAndAssign());
        var llm = new FakeLlm { Inventory = LockerInventory(), Design = GoodDesign(), PermissionFails = true };

        var (ok, project) = await PrepareAllAsync(llm);

        Assert.True(ok);
        Assert.NotNull(project.UxBlueprint);
        Assert.Null(project.PermissionMatrix);
    }

    // Lượt chat KHÔNG còn thiết kế hay soạn quyền: không lời gọi danh mục nào, và ngữ cảnh mang lệnh "đừng hỏi
    // lẻ" phân quyền (bảng chưa có) thay vì khối bảng.
    [Fact]
    public async Task AChatTurn_NoLongerDesignsAnything()
    {
        SeedProject(RequestAndAssign());
        var llm = new FakeLlm { Inventory = LockerInventory(), Design = GoodDesign() };

        await using var db = NewDb();
        await NewSut(db, llm).ChatAsync(_projectId, "Nhân viên tự vào app xin tủ.");

        Assert.Equal(0, llm.InventoryCalls);
        Assert.Equal("", llm.PermissionDraftInput);
        Assert.Contains(llm.ChatSystemMessages, m => m == BAChatPromptBlocks.PermissionMatrixDeferred);
        Assert.DoesNotContain(llm.ChatSystemMessages, m => m.StartsWith("## Bản thiết kế màn hình của ứng dụng", StringComparison.Ordinal));
    }

    // Tác vụ MỚI sau lần soạn đầu ⇒ chỉ thêm dòng cho nó; ô người dùng đã sửa ở popup giữ nguyên.
    [Fact]
    public async Task ANewTask_GetsItsOwnPermissionRow_WithoutTouchingTheUsersEdits()
    {
        var bp = new UxBlueprint
        {
            Tasks = { new UxTask { Id = "T-01", Name = "Gửi ticket", Placed = true }, new UxTask { Id = "T-02", Name = "Huỷ ticket", Placed = true } },
            Screens = { new UxScreen { Id = "S-01", Name = "Locker Tickets", Actions = { new UxAction { Task = "T-01", Label = "Gửi" }, new UxAction { Task = "T-02", Label = "Huỷ" } } } }
        };
        var edited = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new PermissionMatrixRow { TaskId = "T-01", Screen = "Locker Tickets", Function = "Gửi ticket", Grants = { new PermissionGrant { Role = "Nhân viên", Scope = PermissionScope.Unit } } }
        });
        SeedProject(RequestAndAssign(), blueprint: UxBlueprintBuilder.Serialize(bp), permissionMatrix: edited);
        var llm = new FakeLlm
        {
            PermissionRows =
            {
                new PermissionMatrixRow { TaskId = "T-01", Grants = { new PermissionGrant { Role = "Nhân viên", Scope = PermissionScope.All } } },
                new PermissionMatrixRow { TaskId = "T-02", Grants = { new PermissionGrant { Role = "Nhân viên", Scope = PermissionScope.Own } } }
            }
        };

        PermissionMatrixOutcome outcome;
        await using (var db = NewDb())
        {
            var project = await db.Projects.SingleAsync(p => p.Id == _projectId);
            outcome = await new PermissionMatrixDraftService(db, llm, new StubPrompts()).UpdateAsync(project, _ba, _model);
        }

        Assert.Equal(PermissionMatrixOutcome.Patched, outcome);
        Assert.Contains("Các tác vụ MỚI cần thêm dòng", llm.PermissionDraftInput);
        await using var fresh = NewDb();
        var rows = PermissionMatrixBuilder.Parse((await fresh.Projects.SingleAsync(p => p.Id == _projectId)).PermissionMatrix);
        Assert.Equal(PermissionScope.Unit, rows.Single(r => r.TaskId == "T-01").Grants.Single().Scope);
        Assert.Equal(PermissionScope.Own, rows.Single(r => r.TaskId == "T-02").Grants.Single().Scope);
    }

    // Thiết kế lại đổi tên màn ⇒ dòng phân quyền đi theo TÁC VỤ, chỉ cột hiển thị đổi.
    [Fact]
    public void Redesign_RebindsPermissionRowsByTask()
    {
        var before = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new PermissionMatrixRow { TaskId = "T-01", Screen = "Old Requests", Function = "Gửi ticket", Grants = { new PermissionGrant { Role = "Nhân viên", Scope = PermissionScope.Own } } },
            new PermissionMatrixRow { TaskId = "T-09", Screen = "Old Requests", Function = "Tác vụ đã bỏ", Grants = { new PermissionGrant { Role = "Nhân viên", Scope = PermissionScope.Own } } }
        });
        var bp = new UxBlueprint
        {
            Tasks = { new UxTask { Id = "T-01", Name = "Gửi ticket xin tủ" } },
            Screens = { new UxScreen { Id = "S-02", Name = "Locker Tickets", Actions = { new UxAction { Task = "T-01" } } } }
        };

        var rows = PermissionMatrixBuilder.Parse(PermissionMatrixBuilder.Rebind(before, bp));

        var row = Assert.Single(rows);
        Assert.Equal(("T-01", "Locker Tickets", "Gửi ticket xin tủ"), (row.TaskId, row.Screen, row.Function));
        Assert.Equal(PermissionScope.Own, row.Grants.Single().Scope);
    }

    private async Task<(UxDesignOutcome Outcome, Project Project)> PrepareDesignAsync(FakeLlm llm)
    {
        UxDesignOutcome outcome;
        await using (var db = NewDb())
        {
            var project = await db.Projects.SingleAsync(p => p.Id == _projectId);
            outcome = await new UxDesignService(db, llm, new StubPrompts()).PrepareAsync(project, _ba, _model);
        }

        await using var fresh = NewDb();
        return (outcome, await fresh.Projects.SingleAsync(p => p.Id == _projectId));
    }

    private async Task<(bool Ok, Project Project)> PrepareAllAsync(FakeLlm llm)
    {
        bool ok;
        await using (var db = NewDb())
        {
            var prompts = new StubPrompts();
            var project = await db.Projects.Include(p => p.Conversations).SingleAsync(p => p.Id == _projectId);
            ok = await new DesignPreparationService(
                    new ReportMapDraftService(db, llm, prompts),
                    new UxDesignService(db, llm, prompts),
                    new PermissionMatrixDraftService(db, llm, prompts))
                .PrepareAsync(project, _ba, _model);
        }

        await using var fresh = NewDb();
        return (ok, await fresh.Projects.SingleAsync(p => p.Id == _projectId));
    }

    private void AddUserTurn(string message)
    {
        using var db = NewDb();
        db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = _projectId, AgentId = _ba.Id, Role = "user", Message = message, CreatedAt = DateTime.UtcNow.AddSeconds(5)
        });
        db.SaveChanges();
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
            new ReportMapDraftService(db, llm, prompts),
            new ChecklistNoteStore(db, TestOrgChart.NewProvider(db)),
            scopeFactory: null,
            turnTracker: null);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    // Các lời gọi có ý nghĩa: danh mục (+ hỏi lại bước mồ côi), thiết kế (+ phản biện), soạn quyền, lượt chat.
    // Mọi lời gọi phụ khác (bản đồ bao phủ, bộ nhớ…) đều fail-open nên để hỏng hết.
    private sealed class FakeLlm : ILlmClient
    {
        public UxInventoryDraft Inventory = new();
        public UxInventoryDraft? OrphanAnswer;
        public UxBlueprintDraft Design = new();
        public UxBlueprintDraft? Revision;
        public List<PermissionMatrixRow> PermissionRows = new();
        public bool InventoryFails;
        public bool DesignFails;
        public bool PermissionFails;

        public int InventoryCalls;
        public int OrphanCalls;
        public int RevisionCalls;
        public string InventoryInput = "";
        public string OrphanDemand = "";
        public string DesignInput = "";
        public string RevisionDemand = "";
        public string PermissionDraftInput = "";
        public List<string> ChatSystemMessages = new();

        public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCallResult { IsSuccess = false, ErrorMessage = "not used in this test" });

        public Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
        {
            var ok = new LlmCallResult { IsSuccess = true, Content = "{}" };
            var fail = new LlmCallResult { IsSuccess = false, ErrorMessage = "boom" };
            var last = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;

            switch (logContext.Purpose)
            {
                case "BAUxInventory":
                    InventoryCalls++;
                    InventoryInput = last;
                    return InventoryFails ? Fail() : Done(ok, Inventory);

                case "BAUxInventoryOrphans":
                    OrphanCalls++;
                    OrphanDemand = last;
                    return Done(ok, OrphanAnswer ?? new UxInventoryDraft());

                case "UiUxBlueprint":
                    DesignInput = last;
                    return DesignFails ? Fail() : Done(ok, Design);

                case "UiUxBlueprintRevision":
                    RevisionCalls++;
                    RevisionDemand = last;
                    return Done(ok, Revision ?? Design);

                case "BAPermissionMatrixDraft":
                case "BAPermissionMatrixPatch":
                    PermissionDraftInput = last;
                    return PermissionFails ? Fail() : Done(ok, new PermissionMatrixDraft { Rows = PermissionRows });

                case "BAChat":
                    ChatSystemMessages = messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text ?? string.Empty).ToList();
                    return Done(ok, new BAChatReply
                    {
                        Message = "Mỗi người được giữ tối đa mấy tủ?",
                        Suggestions = new List<string> { "Một tủ", "Hai tủ" }
                    });
            }

            return Fail();

            Task<(LlmCallResult, T?)> Fail() => Task.FromResult((fail, (T?)null));
            static Task<(LlmCallResult, T?)> Done(LlmCallResult result, object value) => Task.FromResult((result, (T?)value));
        }
    }

    private sealed class StubPrompts : PromptTemplateService
    {
        public StubPrompts() : base(null!) { }
        public override string Get(string relativePath) => "## prompt stub";
    }
}
