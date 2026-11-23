using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Danh mục đối tượng + tác vụ (tầng 1–2 của bản thiết kế UX). Ba bất biến phải giữ bằng test, vì bảng phân
// quyền khoá theo ID tác vụ:
//  1. ID ỔN ĐỊNH — tác vụ đã có giữ đúng ID qua mọi lần thiết kế lại, kể cả khi model diễn đạt lại tên; ID
//     đã cấp không bao giờ được dùng lại cho việc khác.
//  2. Ô "phục vụ bước" nhận CHỮ CỦA BẢNG LUỒNG, không chữ model gõ lại.
//  3. Tác vụ báo cáo gieo TẤT ĐỊNH từ bảng báo cáo; tác vụ cũ chỉ bị bỏ khi nguồn của nó biến mất.
public class UxInventoryBuilderTests
{
    private const string Flow = """
        [{"name":"Xin tủ","kind":"main","role":"Nhân viên","steps":[
          {"actor":"Nhân viên","action":"Nhân viên gửi ticket xin tủ","outcome":"Pending","included":true},
          {"actor":"FCM","action":"FCM cấp tủ cho nhân viên","outcome":"Done","included":true},
          {"actor":"HR","action":"HR gửi file Excel","outcome":"","included":true,"outsideSoftware":true}]}]
        """;

    private const string Reports = """
        [{"report":"Tủ trống theo khu","question":"còn bao nhiêu tủ","screen":"Locker Dashboard","included":true},
         {"report":"Báo cáo đã bỏ","included":false}]
        """;

    private static UxInventoryDraft Draft(params UxTaskDraft[] tasks) => new()
    {
        Objects = { new UxObject { Name = "Locker Ticket", States = { "Pending", "Done" } } },
        Tasks = tasks.ToList()
    };

    private static UxTaskDraft Task(string name, params string[] steps)
        => new() { Name = name, Object = "Locker Ticket", Kind = "create", Actors = { "Nhân viên" }, FlowSteps = steps.ToList() };

    [Fact]
    public void Build_AssignsSequentialIds_AndSeedsOneTaskPerKeptReport()
    {
        var (_, tasks) = UxInventoryBuilder.Build(null, Draft(Task("Gửi ticket", "Nhân viên gửi ticket xin tủ")), Flow, Reports);

        Assert.Equal(new[] { "T-01", "T-02" }, tasks.Select(t => t.Id));
        var report = tasks.Single(t => t.Kind == UxTaskKind.Report);
        Assert.Equal("Tủ trống theo khu — còn bao nhiêu tủ", report.Name);
        Assert.DoesNotContain(tasks, t => t.Name.Contains("đã bỏ"));
    }

    // Model diễn đạt lại tên của đúng một việc (cùng đối tượng, cùng loại, cùng bước) ⇒ vẫn là tác vụ cũ, giữ ID.
    [Fact]
    public void Build_KeepsTheIdOfAnExistingTask_WhenTheModelRephrasesIt()
    {
        var (objects, first) = UxInventoryBuilder.Build(null, Draft(Task("Gửi ticket", "Nhân viên gửi ticket xin tủ")), Flow, null);
        var existing = new UxBlueprint { Objects = objects, Tasks = first };

        var (_, second) = UxInventoryBuilder.Build(existing, Draft(Task("Nhân viên gửi yêu cầu xin tủ", "Nhân viên gửi ticket xin tủ")), Flow, null);

        Assert.Single(second);
        Assert.Equal("T-01", second[0].Id);
    }

    // ID đã cấp không bao giờ dùng lại: tác vụ cũ bị bỏ (bước luồng của nó không còn) thì tác vụ mới vẫn nhận số
    // MỚI — một quyền còn sót của ID cũ sẽ dính sang việc mới mà không ai thấy.
    [Fact]
    public void Build_NeverReusesTheIdOfADroppedTask()
    {
        var existing = new UxBlueprint
        {
            LastTaskNumber = 5,
            Tasks = { new UxTask { Id = "T-05", Name = "Bước đã bỏ", Source = UxTaskSource.Flow, FlowSteps = { "Một bước không còn trong luồng" } } }
        };

        var (_, tasks) = UxInventoryBuilder.Build(existing, Draft(Task("Gửi ticket", "Nhân viên gửi ticket xin tủ")), Flow, null);

        Assert.DoesNotContain(tasks, t => t.Id == "T-05");
        Assert.Equal("T-06", tasks.Single().Id);
    }

    // "Model quên nêu" KHÔNG phải "đã bỏ": tác vụ cũ còn nguồn thì ở lại dù đề xuất mới không nhắc tới.
    [Fact]
    public void Build_KeepsAnExistingTaskTheModelForgot()
    {
        var existing = new UxBlueprint { Tasks = { new UxTask { Id = "T-01", Name = "Quản lý khu vực", Source = UxTaskSource.Catalog } } };

        var (_, tasks) = UxInventoryBuilder.Build(existing, Draft(), Flow, null);

        Assert.Contains(tasks, t => t.Id == "T-01");
    }

    // Ô "phục vụ bước" chép chữ của BẢNG LUỒNG: bản model gõ gần đúng được kéo về đúng bước, bản không khớp bước
    // nào bị bỏ (nếu không, nó là một báo động giả chờ sẵn của phép đối chiếu).
    [Fact]
    public void Build_PullsFlowStepsBackToTheFlowWording()
    {
        var (_, tasks) = UxInventoryBuilder.Build(null, Draft(Task("Cấp tủ", "cấp tủ cho nhân viên", "Một bước bịa")), Flow, null);

        Assert.Equal(new[] { "FCM cấp tủ cho nhân viên" }, tasks.Single().FlowSteps);
    }

    [Fact]
    public void UncoveredSteps_SkipsStepsDoneOutsideTheSoftware()
    {
        var (_, tasks) = UxInventoryBuilder.Build(null, Draft(Task("Gửi ticket", "Nhân viên gửi ticket xin tủ")), Flow, null);

        Assert.Equal(new[] { "FCM cấp tủ cho nhân viên" }, UxInventoryBuilder.UncoveredSteps(tasks, Flow));
    }

    // Chỉ bước ĐANG mồ côi mới được khai "ngoài phần mềm" — không có đường để model gạt một bước đã có tác vụ ra
    // khỏi ứng dụng.
    [Fact]
    public void ResolveOutside_AcceptsOnlyUncoveredSteps()
    {
        var resolved = UxInventoryBuilder.ResolveOutside(new[]
        {
            new UxOutsideStep { Step = "FCM cấp tủ cho nhân viên", Reason = "gọi điện" },
            new UxOutsideStep { Step = "Nhân viên gửi ticket xin tủ", Reason = "không có" }
        }, new[] { "FCM cấp tủ cho nhân viên" });

        Assert.Equal(new[] { "FCM cấp tủ cho nhân viên" }, resolved.Select(r => r.Step));
    }

    [Fact]
    public void AddFallbackTasks_CoversEveryRemainingStep_WithTheStepsActor()
    {
        var tasks = new List<UxTask>();

        UxInventoryBuilder.AddFallbackTasks(tasks, new[] { "FCM cấp tủ cho nhân viên" }, Flow);

        var task = Assert.Single(tasks);
        Assert.Equal(UxTaskKind.Transition, task.Kind);
        Assert.Equal(new[] { "FCM" }, task.Actors);
        Assert.DoesNotContain("FCM cấp tủ cho nhân viên", UxInventoryBuilder.UncoveredSteps(tasks, Flow));
    }

    // Lượt chắt lọc chỉ THÊM, ở trạng thái chưa xếp chỗ — tác vụ trùng một tác vụ đã có thì bỏ.
    [Fact]
    public void AddPending_AddsOnlyNewTasks_AsNotPlaced()
    {
        var blueprint = new UxBlueprint { LastTaskNumber = 3, Tasks = { new UxTask { Id = "T-03", Name = "Gửi ticket", Placed = true } } };

        var merged = UxInventoryBuilder.AddPending(blueprint, new[]
        {
            new UxTaskDraft { Name = "gửi ticket" },
            new UxTaskDraft { Name = "Huỷ ticket", Object = "Locker Ticket", Kind = "delete" }
        }, Flow);

        Assert.NotNull(merged);
        var added = merged!.Tasks.Single(t => t.Name == "Huỷ ticket");
        Assert.Equal("T-04", added.Id);
        Assert.False(added.Placed);
        Assert.Contains(merged.Objects, o => o.Name == "Locker Ticket");
        Assert.Null(UxInventoryBuilder.AddPending(merged, new[] { new UxTaskDraft { Name = "Huỷ ticket" } }, Flow));
    }

    [Fact]
    public void NormalizeKind_FallsBackByTargetState()
    {
        Assert.Equal(UxTaskKind.Transition, UxInventoryBuilder.NormalizeKind("approve", "Approved"));
        Assert.Equal(UxTaskKind.Browse, UxInventoryBuilder.NormalizeKind("???", ""));
        Assert.Equal(UxTaskKind.Import, UxInventoryBuilder.NormalizeKind("Import", ""));
    }

    [Fact]
    public void ReportTasks_ReadTheReportMap()
        => Assert.Single(UxInventoryBuilder.ReportTasks(Reports));

    [Fact]
    public void FlowSteps_KeepFlowOrderAndActors()
    {
        var steps = UxInventoryBuilder.FlowSteps(Flow);

        Assert.Equal(new[] { "Nhân viên", "FCM" }, steps.Select(s => s.Actor));
        Assert.Equal(JsonSerializer.Serialize(steps[0].Flow), JsonSerializer.Serialize("Xin tủ"));
    }

    // Ca thật lộ ra khi chạy verify: HR chỉ làm bước NGOÀI PHẦN MỀM (gửi file qua email) nhưng vẫn được dựng một
    // menu rỗng với trang đích "?". Người làm bước ngoài phần mềm và "Hệ thống" không phải vai của ứng dụng.
    [Fact]
    public void Roles_SkipSystemActorsAndPeopleWhoOnlyActOutsideTheSoftware()
    {
        var roles = UxRoles.Collect(Flow, new[] { new UxTask { Actors = { "Hệ thống", "Admin" } } }, null);

        Assert.Equal(new[] { "Nhân viên", "FCM", "Admin" }, roles);
    }
}
