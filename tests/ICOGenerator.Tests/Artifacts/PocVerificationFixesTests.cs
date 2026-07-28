using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Chiều "đã sửa được gì" giữa hai vòng kiểm — đối trọng của DetectRegressions. Người review vòng thứ hai
// trở đi cần biết lần sửa vừa rồi đụng vào những gì; nếu không họ phải rà lại cả bản demo từ đầu.
public class PocVerificationFixesTests
{
    [Fact]
    public void DetectFixes_NoPrevious_ReturnsEmpty()
    {
        Assert.Empty(PocVerification.DetectFixes(null, new PocVerificationSummary()));
    }

    [Fact]
    public void DetectFixes_RuleWentFromFailToPass_IsReported()
    {
        var previous = new PocVerificationSummary { SelfTestResults = { "FAIL BR-1 — tổng sai" } };
        var current = new PocVerificationSummary { SelfTestResults = { "PASS BR-1 — 81 đúng" } };

        var fixes = PocVerification.DetectFixes(previous, current);

        Assert.Contains(fixes, f => f.Contains("BR-1") && f.Contains("FAIL ở vòng trước → PASS"));
    }

    [Fact]
    public void DetectFixes_NewlyCheckedPassingItem_IsReported()
    {
        var previous = new PocVerificationSummary();
        var current = new PocVerificationSummary { ScenarioResults = { "PASS Nhân viên gửi đơn — ok" } };

        Assert.Contains(PocVerification.DetectFixes(previous, current),
            f => f.Contains("Nhân viên gửi đơn") && f.Contains("mới được kiểm"));
    }

    [Fact]
    public void DetectFixes_StillFailing_IsNotReportedAsFixed()
    {
        var previous = new PocVerificationSummary { SelfTestResults = { "FAIL BR-1 — sai" } };
        var current = new PocVerificationSummary { SelfTestResults = { "FAIL BR-1 — vẫn sai" } };

        Assert.Empty(PocVerification.DetectFixes(previous, current));
    }

    [Fact]
    public void DetectFixes_ReportsClosedIssuesCoverageAndWorkedExamples()
    {
        var previous = new PocVerificationSummary
        {
            OpenIssues = { "a", "b", "c" },
            WorkedExampleIssues = { "WE-1 lệch" },
            WorkedExamplesTotal = 2,
            CoveredScreens = 3,
            SpecScreens = 5,
            CrossScreenIssues = { "lệch số ngày" }
        };
        var current = new PocVerificationSummary
        {
            OpenIssues = { "c" },
            WorkedExamplesTotal = 2,
            CoveredScreens = 5,
            SpecScreens = 5
        };

        var fixes = PocVerification.DetectFixes(previous, current);

        Assert.Contains(fixes, f => f.Contains("2 điểm đã được xử lý"));
        Assert.Contains(fixes, f => f.Contains("Ví dụ đã chốt"));
        Assert.Contains(fixes, f => f.Contains("Độ phủ màn hình"));
        Assert.Contains(fixes, f => f.Contains("Dữ liệu mẫu giữa các màn hình"));
    }

    [Fact]
    public void DetectFixes_UatDriveResults_AreIncluded()
    {
        var previous = new PocVerificationSummary { UatDriveResults = { "FAIL Duyệt đơn — nút chết" } };
        var current = new PocVerificationSummary { UatDriveResults = { "PASS Duyệt đơn — 3 thao tác chạy được" } };

        Assert.Contains(PocVerification.DetectFixes(previous, current),
            f => f.Contains("Kịch bản nghiệm thu (bấm thử)") && f.Contains("Duyệt đơn"));
    }
}
