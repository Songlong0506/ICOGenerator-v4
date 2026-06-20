using ICOGenerator.Services.Workflows;
using Xunit;

namespace ICOGenerator.Tests.Workflows;

public class TestVerdictParserTests
{
    [Theory]
    [InlineData("Báo cáo...\nVERDICT: PASS", TestVerdict.Pass)]
    [InlineData("VERDICT: FAIL", TestVerdict.Fail)]
    [InlineData("verdict: fail", TestVerdict.Fail)]            // hoa/thường
    [InlineData("**VERDICT: FAIL**", TestVerdict.Fail)]        // bold markdown
    [InlineData("VERDICT = PASS", TestVerdict.Pass)]           // dùng '='
    [InlineData("không có dòng verdict nào", TestVerdict.Unknown)]
    [InlineData("", TestVerdict.Unknown)]
    public void Parse_ReadsMarker(string input, TestVerdict expected)
        => Assert.Equal(expected, TestVerdictParser.Parse(input));

    [Fact]
    public void Parse_NullIsUnknown()
        => Assert.Equal(TestVerdict.Unknown, TestVerdictParser.Parse(null));

    [Fact]
    public void Parse_UsesLastMarker_WhenMultiplePresent()
    {
        // Báo cáo có thể nhắc PASS của một suite lẻ phía trên rồi mới chốt FAIL ở cuối.
        var output = "Suite A: VERDICT: PASS\n...\nKết luận chung\nVERDICT: FAIL";
        Assert.Equal(TestVerdict.Fail, TestVerdictParser.Parse(output));
    }
}
