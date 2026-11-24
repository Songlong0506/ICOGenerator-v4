using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

/// <summary>
/// Luật chấm của lượt lái UAT, không cần trình duyệt: "thiếu điều khiển" và "nút chết" chỉ là khuyết tật của
/// POC khi mọi thao tác TRƯỚC đó đều đã làm được. Ca thật: máy không mở được màn chi tiết (bước trước gộp hai
/// thao tác, máy chỉ làm vế đầu), rồi báo thiếu điều khiển của màn chi tiết — cổng UAT mở hai vòng tự sửa
/// cho một POC chạy đúng.
/// </summary>
public class UatScenarioDriveTests
{
    private static UatAction Do(string kind, string target, string value = "") => new() { Kind = kind, Target = target, Value = value };
    private static UatDriveOutcome Got(string status, string label = "", string view = "", int quoted = 0) =>
        new() { Status = status, Label = label, View = view, Quoted = quoted };

    [Fact]
    public void MissingControl_WhileEveryEarlierActionWorked_IsADefect_AndNamesThePath()
    {
        var drive = new UatScenarioDrive("Xem chi tiết", () => 0);
        drive.RecordAction(0, "Mở 'Danh sách' và bấm dòng ĐƠN-09", Do(UatActionKind.Open, "Danh sách"), Got(PocUatDriver.Ok));
        drive.RecordAction(0, "Mở 'Danh sách' và bấm dòng ĐƠN-09", Do(UatActionKind.Click, "ĐƠN-09"), Got(PocUatDriver.Ok));
        drive.RecordAction(1, "Bấm «Lịch sử»", Do(UatActionKind.Click, "Lịch sử"), Got(PocUatDriver.Missing, "Lịch sử", "Chi tiết"));

        var result = drive.Finish([], 0);

        Assert.False(result.Pass);
        Assert.Contains("bước 2", result.Detail);
        Assert.Contains("«Lịch sử»", result.Detail);
        Assert.Contains("'Chi tiết'", result.Detail);
        Assert.Contains("mở màn «Danh sách» ở menu → bấm «ĐƠN-09»", result.Detail);
    }

    [Fact]
    public void AfterAnActionTheMachineCouldNotDo_MissingAndDeadAreNotConcluded()
    {
        var drive = new UatScenarioDrive("Thêm danh mục", () => 0);
        drive.RecordAction(0, "Nhập Tên", Do(UatActionKind.Fill, "Tên", "ABC"), new UatDriveOutcome { Status = PocUatDriver.Unresolved, Note = "không tìm thấy ô nhập mang nhãn này" });
        drive.RecordAction(1, "Bấm Lưu", Do(UatActionKind.Click, "Lưu"), Got(PocUatDriver.Dead, "Lưu"));
        drive.RecordAction(2, "Bấm Xuất", Do(UatActionKind.Click, "Xuất"), Got(PocUatDriver.Missing, "Xuất"));

        var result = drive.Finish([], 0);

        Assert.True(result.Pass, result.Detail);
        Assert.Null(drive.Failure);
        Assert.Contains("không kết luận được từ bước 1", result.Detail);
        Assert.Contains("không tìm thấy ô nhập", result.Detail);
    }

    [Fact]
    public void OpeningAScreenThatIsNotInTheMenu_StopsConcluding()
    {
        var drive = new UatScenarioDrive("Chi tiết", () => 0);
        drive.RecordAction(0, "Mở 'Chi tiết'", Do(UatActionKind.Open, "Chi tiết"), Got(PocUatDriver.Teleport));
        drive.RecordAction(1, "Bấm Duyệt", Do(UatActionKind.Click, "Duyệt"), Got(PocUatDriver.Dead, "Duyệt"));

        Assert.True(drive.Finish([], 0).Pass);
    }

    // Lỗi JS sau khi máy đã mất tin cậy có thể chỉ do máy mở thẳng một màn thiếu ngữ cảnh — không tính.
    [Fact]
    public void JsErrors_CountOnlyWhileTheStateIsTrusted()
    {
        var errors = new List<string> { "lỗi tải trang (trước kịch bản)" };
        var drive = new UatScenarioDrive("Chi tiết", () => errors.Count);
        drive.RecordAction(0, "Mở 'Chi tiết'", Do(UatActionKind.Open, "Chi tiết"), Got(PocUatDriver.Teleport));
        errors.Add("TypeError: cannot read 'code' of undefined");

        Assert.True(drive.Finish(errors, errorsBefore: 1).Pass);

        var trusted = new UatScenarioDrive("Gửi", () => errors.Count);
        trusted.RecordAction(0, "Bấm Gửi", Do(UatActionKind.Click, "Gửi"), Got(PocUatDriver.Ok));
        var result = trusted.Finish(errors, errorsBefore: 1);
        Assert.False(result.Pass);
        Assert.Contains("lỗi JS khi thao tác: TypeError", result.Detail);
    }

    [Fact]
    public void RoleAndScreen_HaveTheirOwnWording()
    {
        var role = new UatScenarioDrive("a", () => 0);
        role.RecordAction(0, "Chọn vai HR", Do(UatActionKind.Role, "HR"), Got(PocUatDriver.Missing));
        Assert.Contains("không có vai «HR» ở khối VIEW AS", role.Failure);

        var screen = new UatScenarioDrive("b", () => 0);
        screen.RecordAction(0, "Mở Báo cáo", Do(UatActionKind.Open, "Báo cáo"), Got(PocUatDriver.Dead, view: "Trang chủ"));
        Assert.Contains("bấm mục menu «Báo cáo» nhưng màn đó không mở (vẫn đang ở 'Trang chủ')", screen.Failure);
    }

    [Fact]
    public void ACheckThatDidNotSeeItsText_IsANoteNotAFailure()
    {
        var drive = new UatScenarioDrive("Duyệt", () => 0);
        drive.RecordAction(0, "Bấm Duyệt", Do(UatActionKind.Click, "Duyệt"), Got(PocUatDriver.Ok));
        drive.RecordAction(1, "Kiểm tra 'Đã duyệt'", Do(UatActionKind.Check, "Đã duyệt"), Got(PocUatDriver.NotSeen, view: "Danh sách"));

        var result = drive.Finish([], 0);
        Assert.True(result.Pass);
        Assert.Contains("lưu ý: bước 2 chưa thấy «Đã duyệt»", result.Detail);
    }

    // Bộ kịch bản cũ: câu nêu một nhãn thì đơn nghĩa (kết luận được như trước); câu nêu nhiều nhãn thì
    // không — "Nhập Name là 'X', để Status là 'Active', bấm Save" có 'X' là dữ liệu, không phải nút.
    [Fact]
    public void LegacyStep_ConcludesOnlyWhenTheSentenceNamesASingleLabel()
    {
        var single = new UatScenarioDrive("a", () => 0);
        single.RecordLegacyStep(0, "Bấm \"Gửi\"", Got(PocUatDriver.Missing, "Gửi", quoted: 1));
        Assert.Contains("không tìm thấy điều khiển nào mang nhãn 'Gửi' trên POC", single.Failure);

        var multi = new UatScenarioDrive("b", () => 0);
        multi.RecordLegacyStep(0, "Nhập Name là 'X', để Status là 'Active', bấm Save", Got(PocUatDriver.Missing, "X", quoted: 2));
        multi.RecordLegacyStep(1, "Bấm \"Xóa\"", Got(PocUatDriver.Dead, "Xóa", quoted: 1));
        Assert.True(multi.Finish([], 0).Pass);

        // Bấm được nhãn đầu của một câu nhiều nhãn: phần còn lại của câu máy không làm ⇒ hết tin cậy.
        var partial = new UatScenarioDrive("c", () => 0);
        partial.RecordLegacyStep(0, "Chọn tab 'Job Title', rồi bấm 'Add'", Got(PocUatDriver.Ok, "Job Title", quoted: 2));
        Assert.False(partial.Trusted);
    }
}
