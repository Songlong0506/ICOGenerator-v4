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

    [Fact]
    public void NoScenarios_ReportsNothing()
    {
        var issues = PocUatCoverage.Check(new UatScenarioSet(), runtimeRan: true, Array.Empty<string>(), "function pocScenarios(){}");

        Assert.Empty(issues);
    }

    [Fact]
    public void ScriptWithoutPocScenarios_IsReportedWithoutBrowser()
    {
        // Môi trường không có Chromium vẫn phải giữ được cổng: "chưa viết hàm nào" là lỗi thấy được từ text.
        var issues = PocUatCoverage.Check(Set("Nhân viên gửi đơn"), runtimeRan: false, Array.Empty<string>(), "function render(){}");

        Assert.Single(issues);
        Assert.Contains("pocScenarios", issues[0]);
    }

    [Fact]
    public void MissingScenario_IsReported()
    {
        var results = new[] { "PASS Quản lý duyệt đơn — ok" };

        var issues = PocUatCoverage.Check(Set("Nhân viên gửi đơn nghỉ phép"), runtimeRan: true, results, "function pocScenarios(){}");

        Assert.Single(issues);
        Assert.Contains("Nhân viên gửi đơn nghỉ phép", issues[0]);
    }

    [Fact]
    public void TitleMatch_IgnoresPunctuationAndCase()
    {
        var results = new[] { "PASS nhân viên gửi đơn nghỉ phép! — ok" };

        var issues = PocUatCoverage.Check(Set("Nhân viên gửi đơn nghỉ phép"), runtimeRan: true, results, "function pocScenarios(){}");

        Assert.Empty(issues);
    }

    [Fact]
    public void MatchedButFailing_IsReportedAsAcceptanceFailure()
    {
        var results = new[] { "FAIL Nhân viên gửi đơn nghỉ phép — đơn không chuyển trạng thái" };

        var issues = PocUatCoverage.Check(Set("Nhân viên gửi đơn nghỉ phép"), runtimeRan: true, results, "function pocScenarios(){}");

        Assert.Single(issues);
        Assert.Contains("đang FAIL", issues[0]);
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
}
