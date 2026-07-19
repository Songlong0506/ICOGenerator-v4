using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

public class PocWorkedExampleOracleTests
{
    [Theory]
    [InlineData("81", "81", true)]
    [InlineData("81", "81.0", true)]
    [InlineData("81", "81 điểm", true)]          // kỳ vọng số, POC kèm đơn vị → khớp theo số
    [InlineData("81 điểm", "81", true)]
    [InlineData("81", "80", false)]
    [InlineData("1,234", "1234", true)]          // dấu phẩy ngăn nghìn
    [InlineData("7.5 ngày", "7.5", true)]
    [InlineData("Đạt", "đạt", true)]             // không số → so chuỗi chuẩn hoá
    [InlineData("Đạt", "Không đạt", true)]       // chứa nhau (chấp nhận — tránh báo nhầm nhãn)
    [InlineData("Đạt", "Trượt", false)]
    public void ValuesMatch(string expected, string computed, bool match)
    {
        Assert.Equal(match, PocWorkedExampleOracle.ValuesMatch(expected, computed));
    }

    [Fact]
    public void Compare_NoIssuesWhenAllComputedMatchExpected()
    {
        var spec = new[]
        {
            new PocWorkedExample("WE-1", "BR-3", "3 mục tiêu 80/90/70 trọng số 50/30/20", "81"),
            new PocWorkedExample("WE-2", null, "cộng ngày phép", "7.5"),
        };
        var poc = new[]
        {
            new PocWorkedExampleResult("WE-1", "81"),
            new PocWorkedExampleResult("WE-2", "7.5 ngày"),
        };

        Assert.Empty(PocWorkedExampleOracle.Compare(spec, poc));
    }

    [Fact]
    public void Compare_ReportsMismatchWithBothValues()
    {
        var spec = new[] { new PocWorkedExample("WE-1", "BR-3", "…", "81") };
        var poc = new[] { new PocWorkedExampleResult("WE-1", "72") };

        var issues = PocWorkedExampleOracle.Compare(spec, poc);

        var issue = Assert.Single(issues);
        Assert.Contains("WE-1", issue);
        Assert.Contains("81", issue);   // kỳ vọng
        Assert.Contains("72", issue);   // POC tính
    }

    [Fact]
    public void Compare_ReportsMissingWhenPocDidNotComputeExample()
    {
        var spec = new[] { new PocWorkedExample("WE-1", "BR-3", "…", "81") };

        var issues = PocWorkedExampleOracle.Compare(spec, System.Array.Empty<PocWorkedExampleResult>());

        Assert.Contains(issues, i => i.Contains("WE-1") && i.Contains("chưa được POC tính"));
    }

    [Fact]
    public void Compare_EmptyWhenNoWorkedExamples()
    {
        Assert.Empty(PocWorkedExampleOracle.Compare(
            System.Array.Empty<PocWorkedExample>(), System.Array.Empty<PocWorkedExampleResult>()));
    }
}
