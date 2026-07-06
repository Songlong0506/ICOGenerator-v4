using ICOGenerator.Services.Evals;
using Xunit;

namespace ICOGenerator.Tests.Evals;

// Judge phải trả {"score": 1-5, "reasoning": "..."}; parser khoan dung code-fence/văn dẫn (JsonExtractor)
// và kẹp điểm ngoài biên về 1–5 thay vì vứt bỏ.
public class EvalJudgeParserTests
{
    [Fact]
    public void TryParse_PlainJson_ReturnsScoreAndReasoning()
    {
        var ok = EvalJudgeParser.TryParse("""{"score": 4, "reasoning": "Đạt 3/4 tiêu chí."}""", out var score, out var reasoning);

        Assert.True(ok);
        Assert.Equal(4, score);
        Assert.Equal("Đạt 3/4 tiêu chí.", reasoning);
    }

    [Fact]
    public void TryParse_JsonInCodeFenceWithProse_StillParses()
    {
        var text = """
            Đây là đánh giá của tôi:
            ```json
            {"score": 2, "reasoning": "Bịa thông tin."}
            ```
            """;

        Assert.True(EvalJudgeParser.TryParse(text, out var score, out _));
        Assert.Equal(2, score);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(6, 5)]
    [InlineData(-3, 1)]
    public void TryParse_OutOfRangeScore_IsClamped(int raw, int expected)
    {
        Assert.True(EvalJudgeParser.TryParse($$"""{"score": {{raw}}, "reasoning": "x"}""", out var score, out _));
        Assert.Equal(expected, score);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("không có json nào ở đây")]
    [InlineData("{\"reasoning\": \"thiếu score\"}")]
    [InlineData("{\"score\": \"bốn\"}")]
    public void TryParse_MissingOrInvalid_ReturnsFalse(string? text)
    {
        Assert.False(EvalJudgeParser.TryParse(text, out _, out _));
    }
}
