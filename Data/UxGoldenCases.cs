using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;

namespace ICOGenerator.Data;

/// <summary>
/// GOLDEN SET của lượt thiết kế UX: đầu vào thiết kế của ba ca thật (rút gọn) đã từng làm bảng màn hình cũ
/// hỏng theo đúng các kiểu mà <see cref="UxBlueprintLint"/> nay bắt — Locker (đổi trạng thái thành màn riêng,
/// "My X"/"All X"), JD Library (vai duyệt có hàng chờ riêng), Training Plan (bốn danh mục đơn giản thành bốn
/// màn CRUD, kế hoạch có danh sách con).
///
/// <para>
/// Dùng ở hai chỗ, cùng một dữ liệu: scenario Prompt Evals cho <c>UiUx/ux-blueprint.v1.md</c>
/// (<see cref="EvalScenariosSeedData"/>, chấm tất định bằng <c>UxBlueprintEvalScorer</c>) và test của lint/chỉ số
/// (bản thiết kế TỐT phải sạch lỗi, bản XẤU phải bị bắt đúng mã luật).
/// </para>
/// </summary>
public static class UxGoldenCases
{
    public sealed record Case(string Name, string Project, string Description, UxDesignInput Input);

    public static IReadOnlyList<Case> All() => new[] { Locker(), JdLibrary(), TrainingPlan() };

    /// <summary>Thông điệp người dùng đúng định dạng production (<see cref="UxDesignService.RenderDesignRequest"/>).</summary>
    public static string Render(Case c) => UxDesignService.RenderDesignRequest(c.Project, c.Description, c.Input);

    public static Case Locker() => new(
        "Locker",
        "Locker Management",
        "Quản lý tủ cá nhân trong phòng thay đồ: nhân viên xin tủ / báo tủ hư, FCM cấp và thu hồi tủ.",
        new UxDesignInput
        {
            Mode = UxDesignMode.Design,
            Roles = { "Nhân viên", "FCM" },
            Objects =
            {
                new UxObject { Name = "Locker", Description = "Tủ cá nhân", States = { "Available", "Assigned", "Need Clean", "Damaged" }, FieldCount = 4 },
                new UxObject { Name = "Locker Ticket", Description = "Phiếu xin tủ hoặc báo tủ hư", States = { "Pending", "Done", "Rejected" }, FieldCount = 3 },
                new UxObject { Name = "Area", Description = "Khu vực đặt tủ", IsCatalog = true, FieldCount = 2 }
            },
            Tasks =
            {
                Task("T-01", "Xem danh sách tủ", "Locker", UxTaskKind.Browse, "FCM"),
                Task("T-02", "Thêm tủ", "Locker", UxTaskKind.Create, "FCM"),
                Task("T-03", "Gửi ticket xin tủ", "Locker Ticket", UxTaskKind.Create, "Nhân viên"),
                Task("T-04", "Báo tủ hư", "Locker Ticket", UxTaskKind.Create, "Nhân viên"),
                Task("T-05", "Xem ticket của mình", "Locker Ticket", UxTaskKind.Browse, "Nhân viên"),
                Task("T-06", "Cấp tủ cho nhân viên", "Locker Ticket", UxTaskKind.Transition, "FCM", "Pending", "Done"),
                Task("T-07", "Tải file nhân viên nghỉ việc", "Locker", UxTaskKind.Import, "FCM"),
                Task("T-08", "Thu hồi tủ", "Locker", UxTaskKind.Transition, "FCM", "Assigned", "Need Clean"),
                Task("T-09", "Xác nhận đã clean tủ", "Locker", UxTaskKind.Transition, "FCM", "Need Clean", "Available"),
                Task("T-10", "Chuyển tủ sang hư sau khi kiểm tra", "Locker", UxTaskKind.Transition, "FCM", "Assigned", "Damaged"),
                Task("T-11", "Quản lý danh mục khu vực", "Area", UxTaskKind.Configure, "FCM"),
                Task("T-12", "Xem ticket cần xử lý", "Locker Ticket", UxTaskKind.Browse, "FCM"),
                Task("T-13", "Tủ trống theo khu — còn bao nhiêu tủ trống ở mỗi khu", "", UxTaskKind.Report),
                Task("T-14", "Ticket đang chờ — bao nhiêu ticket chưa xử lý", "", UxTaskKind.Report)
            },
            Flows =
            {
                Flow("Cấp tủ", FlowKind.Happy,
                    Step("Nhân viên", "Nhân viên gửi ticket xin tủ", "Pending", "T-03"),
                    Step("FCM", "FCM cấp tủ cho nhân viên", "Done", "T-06")),
                Flow("Thu hồi tủ khi nghỉ việc", FlowKind.Happy,
                    Outside("HR", "HR gửi file Excel nhân viên nghỉ việc cho FCM"),
                    Step("FCM", "FCM tải file nhân viên nghỉ việc lên", "", "T-07"),
                    Step("FCM", "FCM thu hồi tủ", "Need Clean", "T-08"),
                    Step("FCM", "FCM bấm Đã clean", "Available", "T-09")),
                Flow("Báo tủ hư", FlowKind.Exception,
                    Step("Nhân viên", "Nhân viên báo tủ hư", "Pending", "T-04"),
                    Step("FCM", "FCM kiểm tra và chuyển tủ sang Damaged", "Damaged", "T-10"))
            },
            Reports =
            {
                new UxDesignInputReport { Task = "T-13", Report = "Tủ trống theo khu", Placement = "dashboard", Screen = "Locker Dashboard", Breakdown = "khu vực" },
                new UxDesignInputReport { Task = "T-14", Report = "Ticket đang chờ", Placement = "counter", Screen = "Locker Dashboard" }
            }
        });

    public static Case JdLibrary() => new(
        "JD Library",
        "JD Library",
        "Thư viện mô tả công việc: Manager soạn JD, HRBP kiểm, HoD duyệt, Manager gán JD cho nhân viên.",
        new UxDesignInput
        {
            Mode = UxDesignMode.Design,
            Roles = { "Manager", "HRBP", "HoD", "Nhân viên" },
            Objects =
            {
                new UxObject { Name = "Job Description", Description = "Mô tả công việc", States = { "Draft", "Pending HRBP", "Pending HoD", "Approved", "Returned" }, FieldCount = 9 },
                new UxObject { Name = "JD Assignment", Description = "JD gán cho một nhân viên", States = { "Assigned", "Acknowledged" }, FieldCount = 2 },
                new UxObject { Name = "Skill", Description = "Kỹ năng dùng trong JD", IsCatalog = true, FieldCount = 2 }
            },
            Tasks =
            {
                Task("T-01", "Xem thư viện JD", "Job Description", UxTaskKind.Browse, "Manager", "HRBP", "HoD"),
                Task("T-02", "Soạn JD mới", "Job Description", UxTaskKind.Create, "Manager"),
                Task("T-03", "Gửi JD cho HRBP kiểm", "Job Description", UxTaskKind.Transition, "Manager", "Draft", "Pending HRBP"),
                Task("T-04", "HRBP xác nhận JD", "Job Description", UxTaskKind.Transition, "HRBP", "Pending HRBP", "Pending HoD"),
                Task("T-05", "Trả JD về cho Manager sửa", "Job Description", UxTaskKind.Transition, "HRBP", "Pending HRBP", "Returned"),
                Task("T-06", "HoD duyệt JD", "Job Description", UxTaskKind.Transition, "HoD", "Pending HoD", "Approved"),
                Task("T-07", "Sửa JD và gửi lại", "Job Description", UxTaskKind.Edit, "Manager"),
                Task("T-08", "Gán JD cho nhân viên dưới quyền", "JD Assignment", UxTaskKind.Create, "Manager"),
                Task("T-09", "Xem danh sách nhân viên dưới quyền", "JD Assignment", UxTaskKind.Browse, "Manager"),
                Task("T-10", "Xem JD của mình", "JD Assignment", UxTaskKind.View, "Nhân viên"),
                Task("T-11", "Xác nhận đã đọc JD", "JD Assignment", UxTaskKind.Transition, "Nhân viên", "Assigned", "Acknowledged"),
                Task("T-12", "Quản lý danh mục kỹ năng", "Skill", UxTaskKind.Configure, "HRBP")
            },
            Flows =
            {
                Flow("Soạn và duyệt JD", FlowKind.Happy,
                    Step("Manager", "Manager soạn JD", "Draft", "T-02"),
                    Step("Manager", "Manager gửi JD cho HRBP", "Pending HRBP", "T-03"),
                    Step("HRBP", "HRBP xác nhận JD", "Pending HoD", "T-04"),
                    Step("HoD", "HoD duyệt JD", "Approved", "T-06")),
                Flow("Gán JD", FlowKind.Happy,
                    Step("Manager", "Xem danh sách nhân viên trực tiếp dưới quyền", "", "T-09"),
                    Step("Manager", "Manager gán JD cho nhân viên", "Assigned", "T-08"),
                    Step("Nhân viên", "Nhân viên xác nhận đã đọc JD", "Acknowledged", "T-11")),
                Flow("JD bị trả về", FlowKind.Exception,
                    Step("HRBP", "HRBP trả JD về", "Returned", "T-05"),
                    Step("Manager", "Sửa JD và gửi lại", "Pending HRBP", "T-07"))
            }
        });

    public static Case TrainingPlan() => new(
        "Training Plan",
        "Safety Training Plan",
        "Kế hoạch đào tạo an toàn năm: HR Admin lập kế hoạch, HoD HR duyệt, mở lớp, nhân viên đăng ký, Manager duyệt đăng ký.",
        new UxDesignInput
        {
            Mode = UxDesignMode.Design,
            Roles = { "HR Admin", "HoD HR", "Manager", "Nhân viên" },
            Objects =
            {
                new UxObject { Name = "Training Plan", Description = "Kế hoạch đào tạo của năm", States = { "Draft", "Submitted", "Approved", "Rejected" }, FieldCount = 5, HasLineItems = true },
                new UxObject { Name = "Training Class", Description = "Một lớp học mở ra từ kế hoạch", States = { "Planned", "Open", "Closed" }, FieldCount = 6 },
                new UxObject { Name = "Registration", Description = "Đăng ký của một nhân viên vào một lớp", States = { "Pending", "Enrolled", "Waitlist" }, FieldCount = 1 },
                new UxObject { Name = "Course", Description = "Khóa học", IsCatalog = true, FieldCount = 4 },
                new UxObject { Name = "Trainer", Description = "Giảng viên", IsCatalog = true, FieldCount = 3 },
                new UxObject { Name = "Room", Description = "Phòng học", IsCatalog = true, FieldCount = 2 },
                new UxObject { Name = "Course Type", Description = "Loại khóa học (bắt buộc / tự chọn)", IsCatalog = true, FieldCount = 1 }
            },
            Tasks =
            {
                Task("T-01", "Lập kế hoạch đào tạo năm", "Training Plan", UxTaskKind.Create, "HR Admin"),
                Task("T-02", "Chỉnh số lớp cần mở cho từng khóa học", "Training Plan", UxTaskKind.Edit, "HR Admin"),
                Task("T-03", "Gửi kế hoạch cho HoD HR duyệt", "Training Plan", UxTaskKind.Transition, "HR Admin", "Draft", "Submitted"),
                Task("T-04", "HoD HR duyệt kế hoạch", "Training Plan", UxTaskKind.Transition, "HoD HR", "Submitted", "Approved"),
                Task("T-05", "HoD HR từ chối kế hoạch", "Training Plan", UxTaskKind.Transition, "HoD HR", "Submitted", "Rejected"),
                Task("T-06", "Tạo lớp từ kế hoạch đã duyệt", "Training Class", UxTaskKind.Create, "HR Admin"),
                Task("T-07", "Mở đăng ký lớp", "Training Class", UxTaskKind.Transition, "HR Admin", "Planned", "Open"),
                Task("T-08", "Xem các lớp đang mở", "Training Class", UxTaskKind.Browse, "Nhân viên", "HR Admin"),
                Task("T-09", "Đăng ký lớp", "Registration", UxTaskKind.Create, "Nhân viên"),
                Task("T-10", "Duyệt đăng ký của nhân viên", "Registration", UxTaskKind.Transition, "Manager", "Pending", "Enrolled"),
                Task("T-11", "Quản lý danh mục khóa học", "Course", UxTaskKind.Configure, "HR Admin"),
                Task("T-12", "Quản lý danh mục giảng viên", "Trainer", UxTaskKind.Configure, "HR Admin"),
                Task("T-13", "Quản lý danh mục phòng học", "Room", UxTaskKind.Configure, "HR Admin"),
                Task("T-14", "Quản lý loại khóa học", "Course Type", UxTaskKind.Configure, "HR Admin"),
                Task("T-15", "Tỷ lệ tham gia theo phòng ban — phòng nào chưa đi học đủ", "", UxTaskKind.Report)
            },
            Flows =
            {
                Flow("Lập và duyệt kế hoạch, mở lớp", FlowKind.Happy,
                    Step("HR Admin", "HR Admin lập kế hoạch đào tạo năm", "Draft", "T-01"),
                    Step("HR Admin", "HR Admin chỉnh số lớp cho từng khóa", "", "T-02"),
                    Step("HR Admin", "HR Admin gửi kế hoạch cho HoD HR", "Submitted", "T-03"),
                    Step("HoD HR", "HoD HR duyệt kế hoạch", "Approved", "T-04"),
                    Step("HR Admin", "HR Admin tạo lớp từ kế hoạch", "Planned", "T-06"),
                    Step("HR Admin", "HR Admin mở đăng ký lớp", "Open", "T-07")),
                Flow("Đăng ký lớp", FlowKind.Happy,
                    Step("Nhân viên", "Nhân viên đăng ký lớp", "Pending", "T-09"),
                    Step("Manager", "Manager duyệt đăng ký", "Enrolled", "T-10")),
                Flow("Kế hoạch bị từ chối", FlowKind.Exception,
                    Step("HoD HR", "HoD HR từ chối kế hoạch", "Rejected", "T-05"),
                    Step("HR Admin", "HR Admin sửa số lớp rồi gửi lại", "Submitted", "T-02"))
            },
            Reports =
            {
                new UxDesignInputReport { Task = "T-15", Report = "Tỷ lệ tham gia theo phòng ban", Placement = "dashboard", Screen = "Training Dashboard", Breakdown = "phòng ban, quý" }
            }
        });

    private static UxDesignInputTask Task(string id, string name, string obj, string kind, params string[] actorsAndStates)
    {
        // Hai phần tử cuối của một tác vụ chuyển trạng thái là (trạng thái nguồn, trạng thái đích).
        var actors = kind == UxTaskKind.Transition ? actorsAndStates[..^2] : actorsAndStates;
        return new UxDesignInputTask
        {
            Id = id,
            Name = name,
            Object = obj,
            Kind = kind,
            Actors = actors.ToList(),
            FromState = kind == UxTaskKind.Transition ? actorsAndStates[^2] : string.Empty,
            ToState = kind == UxTaskKind.Transition ? actorsAndStates[^1] : string.Empty
        };
    }

    private static UxDesignInputFlow Flow(string name, string kind, params UxDesignInputStep[] steps)
        => new() { Name = name, Kind = kind, Steps = steps.ToList() };

    private static UxDesignInputStep Step(string actor, string action, string outcome, string task)
        => new() { Actor = actor, Action = action, Outcome = outcome, Task = task };

    private static UxDesignInputStep Outside(string actor, string action)
        => new() { Actor = actor, Action = action, Outside = true };
}
