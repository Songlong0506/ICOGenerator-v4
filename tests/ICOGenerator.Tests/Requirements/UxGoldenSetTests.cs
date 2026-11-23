using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// GOLDEN SET của thiết kế màn hình (UxGoldenCases). Mỗi ca có một bản thiết kế TỐT — cái một UX designer giỏi
// sẽ làm — và một bản XẤU theo đúng kiểu bảng màn hình cũ (soạn theo câu chữ người dùng kể: một màn cho mỗi
// bước, "My X" + "All X", đổi trạng thái thành trang riêng, mỗi danh mục một màn CRUD). Hai thứ phải đúng:
//  1. Bản TỐT sạch lỗi lint — nếu không, lint đang phạt chính thiết kế nó muốn khuyến khích, và vòng phản biện
//     sẽ đẩy model ra xa bản đúng.
//  2. Bản XẤU bị bắt đúng mã luật, và chỉ số của nó tệ hơn bản tốt — đó là bằng chứng bộ chỉ số phân biệt
//     được hai bản, tức dùng được làm baseline so trước/sau.
public class UxGoldenSetTests
{
    // ── Locker ──────────────────────────────────────────────────────────────────────────────────────

    private static UxBlueprintDraft LockerGood() => new()
    {
        Screens =
        {
            Screen("Locker Dashboard", "dashboard", "", new[] { "FCM" },
                A("T-13", "Tủ trống theo khu", "section"), A("T-14", "Ticket đang chờ", "section")),
            Screen("Locker List", "list-detail", "Locker", new[] { "FCM" },
                A("T-01", "Tìm tủ", "filter"),
                A("T-02", "Thêm tủ", "toolbar", "primary", form: "modal"),
                A("T-07", "Tải file nghỉ việc", "toolbar"),
                A("T-08", "Thu hồi tủ", "row", "primary", "Assigned"),
                A("T-09", "Đã clean", "row", when: "Need Clean"),
                A("T-10", "Chuyển sang hư", "row", "overflow", "Assigned")),
            Screen("Locker Tickets", "list", "Locker Ticket", new[] { "Nhân viên", "FCM" },
                A("T-05", "Ticket của tôi", "filter"),
                A("T-12", "Chờ xử lý", "filter"),
                A("T-03", "Xin tủ", "toolbar", "primary", form: "modal"),
                A("T-04", "Báo tủ hư", "toolbar", form: "modal"),
                A("T-06", "Cấp tủ", "row", "primary", "Pending")),
            Screen("Area Catalog", "list", "Area", new[] { "FCM" }, A("T-11", "Thêm khu", "toolbar", "primary", form: "modal"))
        },
        Navigation =
        {
            Nav("FCM", "Locker Dashboard", "Locker Dashboard", "Locker List", "Locker Tickets", "Area Catalog"),
            Nav("Nhân viên", "Locker Tickets", "Locker Tickets")
        }
    };

    private static UxBlueprintDraft LockerBad() => new()
    {
        Screens =
        {
            Screen("Locker Request", "form", "Locker Ticket", new[] { "Nhân viên" }, A("T-03", "Gửi", "toolbar", "primary")),
            Screen("My Tickets", "list", "Locker Ticket", new[] { "Nhân viên" }, A("T-05", "Xem", "filter")),
            Screen("Ticket Approval", "list", "Locker Ticket", new[] { "FCM" }, A("T-12", "Xem", "filter"), A("T-06", "Cấp tủ", "row", "primary")),
            Screen("Damage Report", "form", "Locker Ticket", new[] { "Nhân viên" }, A("T-04", "Gửi", "toolbar", "primary")),
            Screen("Locker List", "list", "Locker", new[] { "FCM" },
                A("T-01", "Xem", "filter"), A("T-02", "Thêm tủ", "toolbar", "primary"),
                A("T-13", "Tủ trống theo khu", "section"), A("T-14", "Ticket đang chờ", "section")),
            Screen("Resignation Upload", "form", "Locker", new[] { "FCM" }, A("T-07", "Tải file", "toolbar", "primary")),
            Screen("Locker Recovery", "form", "Locker", new[] { "FCM" }, A("T-08", "Thu hồi", "toolbar", "primary")),
            Screen("Cleaning Confirmation", "form", "Locker", new[] { "FCM" }, A("T-09", "Đã clean", "toolbar", "primary")),
            Screen("Damage Handling", "list", "Locker", new[] { "FCM" }, A("T-10", "Chuyển hư", "row", "primary")),
            Screen("Area Catalog", "list", "Area", new[] { "FCM" }, A("T-11", "Thêm khu", "toolbar", "primary"))
        },
        Navigation =
        {
            Nav("FCM", "Locker List", "Ticket Approval", "Locker List", "Resignation Upload", "Locker Recovery",
                "Cleaning Confirmation", "Damage Handling", "Area Catalog"),
            Nav("Nhân viên", "Locker Request", "Locker Request", "My Tickets", "Damage Report")
        }
    };

    [Fact]
    public void Locker_GoodDesign_IsCleanOfErrors_AndFlowsWithoutSwitchingScreens()
    {
        var (bp, findings) = Lint(UxGoldenCases.Locker(), LockerGood());

        Assert.Empty(UxBlueprintLint.Errors(findings));
        Assert.Equal(0, bp.Metrics.AvgScreenSwitchesPerFlow);
        Assert.Equal(4, bp.Metrics.Screens);
    }

    [Fact]
    public void Locker_BadDesign_IsCaughtByTheExpectedRules_AndScoresWorse()
    {
        var (good, _) = Lint(UxGoldenCases.Locker(), LockerGood());
        var (bad, findings) = Lint(UxGoldenCases.Locker(), LockerBad());
        var codes = UxBlueprintLint.Errors(findings).Select(f => f.Code).ToHashSet();

        Assert.Contains(UxLintCode.DuplicateHome, codes);         // My Tickets + Ticket Approval; Locker List + Damage Handling
        Assert.Contains(UxLintCode.TransitionScreen, codes);      // Locker Recovery, Cleaning Confirmation
        Assert.Contains(UxLintCode.FormShouldBeModal, codes);     // Locker Request, Damage Report (3 trường, không danh sách con)
        Assert.Contains(UxLintCode.ReportMisplaced, codes);       // báo cáo đặt trên Locker List
        Assert.Contains(UxLintCode.FragmentedFlow, codes);        // tải file → thu hồi → đã clean: ba màn cho một người, một bản ghi

        Assert.True(bad.Metrics.Screens > good.Metrics.Screens);
        Assert.True(bad.Metrics.AvgScreenSwitchesPerFlow > good.Metrics.AvgScreenSwitchesPerFlow);
        Assert.True(bad.Metrics.ScreensPerObject > good.Metrics.ScreensPerObject);
    }

    // Vòng vá tất định lấp được một bản thiếu: mọi tác vụ có chỗ, mọi vai có menu + trang đích.
    [Fact]
    public void Repair_LeavesNoTaskUnplaced_AndEveryRoleWithALanding()
    {
        var draft = LockerGood();
        draft.Screens.RemoveAt(3);            // bỏ Area Catalog ⇒ T-11 mồ côi
        draft.Navigation.RemoveAt(1);         // bỏ menu của Nhân viên
        var (bp, before) = Lint(UxGoldenCases.Locker(), draft);
        Assert.Contains(before, f => f.Code == UxLintCode.UnplacedTask);

        UxBlueprintBuilder.Repair(bp, UxGoldenCases.Locker().Input.Roles);

        Assert.All(bp.Tasks, t => Assert.NotNull(UxBlueprintBuilder.HomeOf(bp, t.Id)));
        Assert.Contains(bp.Screens, s => s.Name == "Area Catalog" && s.Object == "Area");
        var employee = Assert.Single(bp.Navigation, n => n.Role == "Nhân viên");
        Assert.Equal("Locker Tickets", employee.Landing);
    }

    // ── Training Plan ───────────────────────────────────────────────────────────────────────────────

    private static UxBlueprintDraft TrainingGood() => new()
    {
        Screens =
        {
            Screen("Training Dashboard", "dashboard", "", new[] { "HR Admin", "HoD HR" }, A("T-15", "Tỷ lệ tham gia", "section")),
            Screen("Training Plans", "list", "Training Plan", new[] { "HR Admin" },
                A("T-01", "Lập kế hoạch", "toolbar", "primary", form: "page"),
                A("T-03", "Gửi duyệt", "row", "primary", "Draft")),
            Screen("Training Plan Detail", "detail", "Training Plan", new[] { "HR Admin" }, entry: "drilldown",
                actions: new[] { A("T-02", "Chỉnh số lớp", "inline", "primary") }),
            Screen("Plan Approval", "queue", "Training Plan", new[] { "HoD HR" },
                A("T-04", "Duyệt", "row", "primary", "Submitted"), A("T-05", "Từ chối", "row", "secondary", "Submitted")),
            Screen("Training Classes", "list", "Training Class", new[] { "HR Admin", "Nhân viên" },
                A("T-08", "Tìm lớp", "filter"),
                A("T-06", "Tạo lớp từ kế hoạch", "toolbar", "primary", form: "modal"),
                A("T-07", "Mở đăng ký", "row", "primary", "Planned"),
                A("T-09", "Đăng ký", "row", "secondary", "Open")),
            Screen("Registration Approval", "queue", "Registration", new[] { "Manager" }, A("T-10", "Duyệt đăng ký", "row", "primary", "Pending")),
            Screen("Master Data", "master-data", "", new[] { "HR Admin" }, tabs: new[] { "Course", "Trainer", "Room", "Course Type" },
                actions: new[] { A("T-11", "Khóa học", "section"), A("T-12", "Giảng viên", "section"), A("T-13", "Phòng học", "section"), A("T-14", "Loại khóa học", "section") })
        },
        Navigation =
        {
            Nav("HR Admin", "Training Dashboard", "Training Dashboard", "Training Plans", "Training Classes", "Master Data"),
            Nav("HoD HR", "Plan Approval", "Training Dashboard", "Plan Approval"),
            Nav("Manager", "Registration Approval", "Registration Approval"),
            Nav("Nhân viên", "Training Classes", "Training Classes")
        }
    };

    [Fact]
    public void TrainingPlan_GoodDesign_IsCleanOfErrors()
    {
        var (bp, findings) = Lint(UxGoldenCases.TrainingPlan(), TrainingGood());

        Assert.Empty(UxBlueprintLint.Errors(findings));
        Assert.Equal(0, bp.Metrics.AvgScreenSwitchesPerFlow);
    }

    // Bốn danh mục đơn giản thành bốn màn CRUD giống hệt nhau, và "gửi duyệt" thành một trang riêng.
    [Fact]
    public void TrainingPlan_CatalogSprawlAndTransitionPage_AreCaught()
    {
        var bad = TrainingGood();
        bad.Screens.RemoveAt(6);
        bad.Screens.Add(Screen("Course Catalog", "list", "Course", new[] { "HR Admin" }, A("T-11", "Thêm", "toolbar", "primary")));
        bad.Screens.Add(Screen("Trainer Catalog", "list", "Trainer", new[] { "HR Admin" }, A("T-12", "Thêm", "toolbar", "primary")));
        bad.Screens.Add(Screen("Room Catalog", "list", "Room", new[] { "HR Admin" }, A("T-13", "Thêm", "toolbar", "primary")));
        bad.Screens.Add(Screen("Course Type Catalog", "list", "Course Type", new[] { "HR Admin" }, A("T-14", "Thêm", "toolbar", "primary")));
        bad.Screens[1].Actions.RemoveAt(1);
        bad.Screens.Add(Screen("Plan Submit", "form", "Training Plan", new[] { "HR Admin" }, A("T-03", "Gửi duyệt", "toolbar", "primary")));

        var (_, findings) = Lint(UxGoldenCases.TrainingPlan(), bad);
        var codes = UxBlueprintLint.Errors(findings).Select(f => f.Code).ToHashSet();

        Assert.Contains(UxLintCode.CatalogSprawl, codes);
        Assert.Contains(UxLintCode.TransitionScreen, codes);
    }

    // ── Dữ liệu golden phải tự nhất quán ────────────────────────────────────────────────────────────

    // Mỗi bước luồng (trừ bước ngoài phần mềm) của golden set có tác vụ phụ trách, và mỗi scenario đọc ngược
    // được — nếu không, Prompt Evals chấm model trên một đầu vào hỏng.
    [Fact]
    public void EveryGoldenCase_IsConsistent_AndRoundTripsThroughThePromptFormat()
    {
        foreach (var golden in UxGoldenCases.All())
        {
            var (inventory, flow) = UxDesignInputBuilder.ToInventory(golden.Input);
            Assert.Empty(UxInventoryBuilder.UncoveredSteps(inventory.Tasks, flow));

            var parsed = UxDesignInputBuilder.TryParse(UxGoldenCases.Render(golden));
            Assert.NotNull(parsed);
            Assert.Equal(golden.Input.Tasks.Count, parsed!.Tasks.Count);
        }
    }

    // ── tiện ích ────────────────────────────────────────────────────────────────────────────────────

    private static (UxBlueprint Blueprint, List<UxFinding> Findings) Lint(UxGoldenCases.Case golden, UxBlueprintDraft draft)
    {
        var (inventory, flow) = UxDesignInputBuilder.ToInventory(golden.Input);
        var bp = UxBlueprintBuilder.FromDraft(draft, inventory, golden.Input.Roles);
        bp.Journeys = UxJourneyAnalyzer.Journeys(bp, flow);
        bp.Findings = UxBlueprintLint.Check(bp, flow);
        bp.Metrics = UxJourneyAnalyzer.Metrics(bp, flow);
        return (bp, bp.Findings);
    }

    private static UxScreenDraft Screen(string name, string pattern, string obj, string[] audience, params UxAction[] actions)
        => Screen(name, pattern, obj, audience, entry: "nav", tabs: null, actions: actions);

    private static UxScreenDraft Screen(string name, string pattern, string obj, string[] audience, string entry = "nav", string[]? tabs = null, UxAction[]? actions = null)
        => new()
        {
            Name = name,
            Pattern = pattern,
            Object = obj,
            Purpose = name,
            Audience = audience.ToList(),
            Entry = entry,
            Tabs = (tabs ?? Array.Empty<string>()).ToList(),
            Actions = (actions ?? Array.Empty<UxAction>()).ToList()
        };

    private static UxAction A(string task, string label, string placement, string prominence = "secondary", string when = "", string form = "none")
        => new() { Task = task, Label = label, Placement = placement, Prominence = prominence, WhenState = when, Form = form };

    private static UxNavigation Nav(string role, string landing, params string[] screens)
        => new() { Role = role, Landing = landing, Items = screens.Select(s => new UxNavItem { Label = s, Screen = s }).ToList() };
}
