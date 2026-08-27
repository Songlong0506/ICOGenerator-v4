using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

public class PocUatAnchorsTests
{
    private static UatScenarioSet Set(params int[] stepCounts) => new()
    {
        Scenarios = stepCounts.Select((count, i) => new UatScenario
        {
            Title = $"Kịch bản {i + 1}",
            Screen = "Đơn nghỉ phép",
            Steps = Enumerable.Range(1, count).Select(j => $"Bước {j}").ToList()
        }).ToList()
    };

    // Neo đủ cho mọi bước ⇒ sạch. Đây cũng là chỗ chốt quy ước đánh số 1-based: agent gắn "1.1" cho bước
    // đầu tiên của kịch bản đầu tiên, đúng cách khối prompt in ra.
    [Fact]
    public void EveryStepAnchored_ReportsNothing()
    {
        var html = """<button data-uat="1.1">Gửi</button><span data-uat="1.2">Đã gửi</span>""";

        Assert.Empty(PocUatAnchors.Check(Set(2), html));
    }

    [Fact]
    public void NoScenarios_ReportsNothing()
    {
        Assert.Empty(PocUatAnchors.Check(new UatScenarioSet(), """<button data-uat="9.9">x</button>"""));
    }

    // POC dựng trước khi có cơ chế neo: MỘT dòng cho cả bộ, không phải mỗi bước một dòng — vài chục issue
    // cho cùng một việc sẽ đẩy các issue thật ra khỏi tầm chú ý của agent.
    [Fact]
    public void NoAnchorAtAll_ReportsExactlyOneIssue()
    {
        var issues = PocUatAnchors.Check(Set(3, 4), "<button>Gửi</button>");

        Assert.Single(issues);
        Assert.Contains("data-uat", issues[0]);
    }

    [Fact]
    public void MissingStep_IsReportedWithItsToken()
    {
        var html = """<button data-uat="1.1">Gửi</button>""";

        var issues = PocUatAnchors.Check(Set(2), html);

        Assert.Single(issues);
        Assert.Contains("1.2", issues[0]);
        Assert.Contains("Kịch bản 1", issues[0]);
    }

    // Một phần tử phục vụ nhiều bước: đúng ngữ nghĩa [data-uat~="..."] mà trang review và lượt lái dùng.
    [Fact]
    public void SpaceSeparatedTokens_CountForEveryStepListed()
    {
        var html = """<button data-uat="1.1 2.1">Gửi</button><span data-uat='1.2'>ok</span><span data-uat="2.2">ok</span>""";

        Assert.Empty(PocUatAnchors.Check(Set(2, 2), html));
    }

    // Agent đánh số lệch (0-based) là lỗi IM LẶNG tệ nhất: mọi bước đều "có neo" theo mắt người đọc HTML
    // nhưng không bước nào tra ra phần tử. Cổng phải bắt cả hai phía — bước thiếu VÀ mã thừa.
    [Fact]
    public void ZeroBasedNumbering_IsReportedAsStrayAnchor()
    {
        var html = """<button data-uat="0.0">Gửi</button><span data-uat="0.1">Đã gửi</span>""";

        var issues = PocUatAnchors.Check(Set(2), html);

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, x => x.Contains("0.0") && x.Contains("không ứng với bước nào"));
    }

    [Fact]
    public void Collect_ReadsBothQuoteStyles()
    {
        var tokens = PocUatAnchors.Collect("""<i data-uat="1.1"></i><i data-uat='2.3'></i>""");

        Assert.Equal(new[] { "1.1", "2.3" }, tokens.OrderBy(x => x, StringComparer.Ordinal));
    }
}
