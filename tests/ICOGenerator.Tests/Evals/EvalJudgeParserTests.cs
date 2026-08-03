using ICOGenerator.Services.Evals;
using Xunit;

namespace ICOGenerator.Tests.Evals;

// Judge phải trả {"score": 1-5, "reasoning": "...", "criteria": [...]}; parser khoan dung code-fence/văn
// dẫn (LlmJson) và kẹp điểm ngoài biên về 1–5 thay vì vứt bỏ. Phần criteria là MỞ RỘNG: thiếu nó vẫn
// parse được (kết quả cũ trong DB, model không chịu nghe) — mất bảng đối chiếu chứ không mất điểm.
public class EvalJudgeParserTests
{
    [Fact]
    public void TryParse_PlainJson_ReturnsScoreAndReasoning()
    {
        var ok = EvalJudgeParser.TryParse("""{"score": 4, "reasoning": "Đạt 3/4 tiêu chí."}""", out var verdict);

        Assert.True(ok);
        Assert.Equal(4, verdict.Score);
        Assert.Equal("Đạt 3/4 tiêu chí.", verdict.Reasoning);
        Assert.Empty(verdict.Criteria);
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

        Assert.True(EvalJudgeParser.TryParse(text, out var verdict));
        Assert.Equal(2, verdict.Score);
    }

    [Fact]
    public void TryParse_Criteria_KeepsOrderPassFlagAndNote()
    {
        var text = """
            {"score": 3, "reasoning": "Trượt định dạng.",
             "criteria": [
               {"criterion": "Trả về JSON hợp lệ", "passed": false, "note": "Có lời dẫn trước JSON."},
               {"criterion": "ready = false", "passed": true, "note": ""}
             ]}
            """;

        Assert.True(EvalJudgeParser.TryParse(text, out var verdict));
        Assert.Collection(verdict.Criteria,
            first =>
            {
                Assert.Equal("Trả về JSON hợp lệ", first.Criterion);
                Assert.False(first.Passed);
                Assert.Equal("Có lời dẫn trước JSON.", first.Note);
            },
            second =>
            {
                Assert.Equal("ready = false", second.Criterion);
                Assert.True(second.Passed);
                Assert.Null(second.Note); // note rỗng ⇒ null, UI khỏi phải hiện ô trống
            });
    }

    [Fact]
    public void TryParse_CriteriaRows_MissingNameDropped_MissingPassedCountsAsFail()
    {
        var text = """
            {"score": 4, "reasoning": "x",
             "criteria": [
               {"criterion": "  ", "passed": true},
               {"criterion": "Không bịa thông tin"}
             ]}
            """;

        Assert.True(EvalJudgeParser.TryParse(text, out var verdict));
        var only = Assert.Single(verdict.Criteria);
        Assert.Equal("Không bịa thông tin", only.Criterion);
        Assert.False(only.Passed); // thiếu "passed" thì coi như trượt, không mặc định cho đạt
    }

    [Fact]
    public void RoundTrip_CriteriaJson_ReadsBackSameRows()
    {
        Assert.True(EvalJudgeParser.TryParse(
            """{"score": 5, "reasoning": "ổn", "criteria": [{"criterion": "A", "passed": true}]}""", out var verdict));

        var json = EvalJudgeParser.ToJson(verdict.Criteria);
        Assert.NotNull(json);

        var readBack = Assert.Single(EvalJudgeParser.ReadCriteriaJson(json));
        Assert.Equal("A", readBack.Criterion);
        Assert.True(readBack.Passed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("không phải json")]
    public void ReadCriteriaJson_MissingOrInvalid_ReturnsEmpty(string? json)
        => Assert.Empty(EvalJudgeParser.ReadCriteriaJson(json));

    [Fact]
    public void ToJson_EmptyCriteria_ReturnsNull()
        => Assert.Null(EvalJudgeParser.ToJson(Array.Empty<EvalCriterionVerdict>()));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(6, 5)]
    [InlineData(-3, 1)]
    public void TryParse_OutOfRangeScore_IsClamped(int raw, int expected)
    {
        Assert.True(EvalJudgeParser.TryParse($$"""{"score": {{raw}}, "reasoning": "x"}""", out var verdict));
        Assert.Equal(expected, verdict.Score);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("không có json nào ở đây")]
    [InlineData("{\"reasoning\": \"thiếu score\"}")]
    [InlineData("{\"score\": \"bốn\"}")]
    public void TryParse_MissingOrInvalid_ReturnsFalse(string? text)
    {
        Assert.False(EvalJudgeParser.TryParse(text, out _));
    }
}
