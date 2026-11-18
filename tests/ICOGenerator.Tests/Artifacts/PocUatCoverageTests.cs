using ICOGenerator.Contracts.Requirements;
using Xunit;
using ICOGenerator.Services.Artifacts;

namespace ICOGenerator.Tests.Artifacts;

public class PocUatCoverageTests
{
    private static UatScenarioSet Set(params string[] titles) => new()
    {
        Scenarios = titles.Select(t => new UatScenario
        {
            Title = t,
            Screen = "Đơn nghỉ phép",
            Steps = new List<string> { "Bấm \"Gửi đơn\"", "Kiểm tra trạng thái" }
        }).ToList()
    };

    private static readonly IReadOnlyList<PocUatDriveResult> NoDrive = Array.Empty<PocUatDriveResult>();

    [Fact]
    public void NoScenarios_ReportsNothing()
    {
        var issues = PocUatCoverage.Check(new UatScenarioSet(), runtimeRan: true, Array.Empty<string>(), NoDrive, "function pocScenarios(){}");

        Assert.Empty(issues);
    }

    [Fact]
    public void ScriptWithoutPocScenarios_IsReportedWithoutBrowser()
    {
        // Môi trường không có Chromium vẫn phải giữ được cổng: "chưa viết hàm nào" là lỗi thấy được từ text.
        var issues = PocUatCoverage.Check(Set("Nhân viên gửi đơn"), runtimeRan: false, Array.Empty<string>(), NoDrive, "function render(){}");

        Assert.Single(issues);
        Assert.Contains("pocScenarios", issues[0]);
    }

    [Fact]
    public void MissingScenario_IsReported()
    {
        var results = new[] { "PASS Quản lý duyệt đơn — ok" };

        var issues = PocUatCoverage.Check(Set("Nhân viên gửi đơn nghỉ phép"), runtimeRan: true, results, NoDrive, "function pocScenarios(){}");

        Assert.Single(issues);
        Assert.Contains("Nhân viên gửi đơn nghỉ phép", issues[0]);
    }

    [Fact]
    public void TitleMatch_IgnoresPunctuationAndCase()
    {
        var results = new[] { "PASS nhân viên gửi đơn nghỉ phép! — ok" };

        var issues = PocUatCoverage.Check(Set("Nhân viên gửi đơn nghỉ phép"), runtimeRan: true, results, NoDrive, "function pocScenarios(){}");

        Assert.Empty(issues);
    }

    [Fact]
    public void MatchedButFailing_IsReportedAsAcceptanceFailure()
    {
        var results = new[] { "FAIL Nhân viên gửi đơn nghỉ phép — đơn không chuyển trạng thái" };

        var issues = PocUatCoverage.Check(Set("Nhân viên gửi đơn nghỉ phép"), runtimeRan: true, results, NoDrive, "function pocScenarios(){}");

        Assert.Single(issues);
        Assert.Contains("báo FAIL", issues[0]);
        Assert.Contains("đơn không chuyển trạng thái", issues[0]);
    }

    [Fact]
    public void CountPassing_CountsOnlyMatchedPassingScenarios()
    {
        var results = new[]
        {
            "PASS Nhân viên gửi đơn nghỉ phép — ok",
            "FAIL Quản lý duyệt đơn — sai trạng thái"
        };

        var count = PocUatCoverage.CountPassing(Set("Nhân viên gửi đơn nghỉ phép", "Quản lý duyệt đơn", "Nhân viên xem lịch sử"), results);

        Assert.Equal(1, count);
    }

    [Fact]
    public void BrokenScenario_FailingInScenariosAndDrive_IsOneIssue()
    {
        // Một kịch bản gãy ở cả hai tầng (mục pocScenarios() FAIL + bấm thử không được) là MỘT điểm chưa
        // đạt, không phải hai — nếu không thì "Điểm máy báo chưa đạt" đếm cùng một chỗ gãy nhiều lần.
        var results = new[] { "FAIL Nhân viên gửi đơn nghỉ phép — đơn không chuyển trạng thái" };
        var drive = new[] { new PocUatDriveResult("Nhân viên gửi đơn nghỉ phép", false, "bấm 'Gửi đơn' nhưng màn hình không thay đổi gì") };

        var issues = PocUatCoverage.Check(Set("Nhân viên gửi đơn nghỉ phép"), runtimeRan: true, results, drive, "function pocScenarios(){}");

        var issue = Assert.Single(issues);
        Assert.Contains("đơn không chuyển trạng thái", issue);
        Assert.Contains("KHÔNG thao tác được", issue);
    }

    [Fact]
    public void DriveFailure_AloneIsReported()
    {
        var results = new[] { "PASS Nhân viên gửi đơn nghỉ phép — ok" };
        var drive = new[] { new PocUatDriveResult("Nhân viên gửi đơn nghỉ phép", false, "không tìm thấy điều khiển 'Gửi đơn'") };

        var issues = PocUatCoverage.Check(Set("Nhân viên gửi đơn nghỉ phép"), runtimeRan: true, results, drive, "function pocScenarios(){}");

        var issue = Assert.Single(issues);
        Assert.Contains("KHÔNG thao tác được", issue);
        Assert.DoesNotContain("báo FAIL", issue);
    }

    [Fact]
    public void FailingFlowOutsideUat_IsReportedSeparately()
    {
        // Luồng agent tự đặt ngoài bộ UAT không gắn với kịch bản nào — vẫn phải thành issue (runtime không
        // còn tự sinh issue cho dòng FAIL của pocScenarios()), kể cả khi dự án không có UAT.
        var results = new[]
        {
            "PASS Nhân viên gửi đơn nghỉ phép — ok",
            "FAIL Xuất báo cáo tháng — thiếu cột tổng"
        };

        var withUat = PocUatCoverage.Check(Set("Nhân viên gửi đơn nghỉ phép"), runtimeRan: true, results, NoDrive, "function pocScenarios(){}");
        var withoutUat = PocUatCoverage.Check(new UatScenarioSet(), runtimeRan: true, results, NoDrive, "function pocScenarios(){}");

        Assert.Contains("end-to-end", Assert.Single(withUat));
        Assert.Contains("Xuất báo cáo tháng", Assert.Single(withoutUat));
    }

    [Fact]
    public void ScriptWithoutPocScenarios_DoesNotRepeatMissingEntryPerScenario()
    {
        // Hàm chưa có ⇒ một issue tĩnh cho cả bộ; lượt bấm thử hỏng vẫn được gộp vào issue của kịch bản đó.
        var drive = new[] { new PocUatDriveResult("Quản lý duyệt đơn", false, "nút chết") };

        var issues = PocUatCoverage.Check(Set("Nhân viên gửi đơn", "Quản lý duyệt đơn"), runtimeRan: true,
            Array.Empty<string>(), new[] { new PocUatDriveResult("Nhân viên gửi đơn", true, "ok"), drive[0] }, "function render(){}");

        Assert.Equal(2, issues.Count);
        Assert.Contains("chưa định nghĩa window.pocScenarios()", issues[0]);
        Assert.Contains("Quản lý duyệt đơn", issues[1]);
        Assert.DoesNotContain("KHÔNG có mục tương ứng", issues[1]);
    }

    [Fact]
    public void OutsideUat_ExcludesLinesMatchedToUatScenarios()
    {
        var results = new[]
        {
            "PASS Nhân viên gửi đơn nghỉ phép — ok",
            "FAIL Quản lý duyệt đơn — sai trạng thái",
            "PASS Xuất báo cáo tháng — ok"
        };

        var outside = PocUatCoverage.OutsideUat(Set("Nhân viên gửi đơn nghỉ phép", "Quản lý duyệt đơn"), results);

        Assert.Equal(new[] { "PASS Xuất báo cáo tháng — ok" }, outside);
    }
}
