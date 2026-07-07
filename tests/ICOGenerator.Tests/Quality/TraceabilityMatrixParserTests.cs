using ICOGenerator.Services.Quality;
using Xunit;

namespace ICOGenerator.Tests.Quality;

// Parser ma trận truy vết phải khoan dung (code-fence/văn dẫn, thiếu mảng, status lạ) nhưng KHÔNG chấp
// nhận ma trận rỗng — không có dòng yêu cầu nào thì trả false để người dùng chạy lại thay vì lưu rác.
public class TraceabilityMatrixParserTests
{
    [Fact]
    public void TryParse_FullJson_ParsesAllSections()
    {
        var ok = TraceabilityMatrixParser.TryParse("""
            {
              "requirements": [
                {
                  "code": "R-01",
                  "title": "Đăng nhập SSO",
                  "kind": "Chức năng",
                  "stories": ["US-01 — Đăng nhập"],
                  "codeFiles": ["src/auth/login.ts"],
                  "tests": ["TC-03 đăng nhập thành công"],
                  "status": "covered",
                  "note": ""
                }
              ],
              "orphanStories": [ { "story": "US-09 — Xuất PDF", "reason": "không khớp yêu cầu nào" } ],
              "summary": "Phủ tốt."
            }
            """, out var matrix);

        Assert.True(ok);
        var row = Assert.Single(matrix!.Requirements);
        Assert.Equal("R-01", row.Code);
        Assert.Equal("Đăng nhập SSO", row.Title);
        Assert.Equal(TraceabilityMatrix.StatusCovered, row.Status);
        Assert.Single(row.Stories);
        Assert.Single(row.CodeFiles);
        Assert.Single(row.Tests);
        Assert.Null(row.Note); // note rỗng chuẩn hoá về null
        var orphan = Assert.Single(matrix.OrphanStories);
        Assert.Equal("US-09 — Xuất PDF", orphan.Story);
        Assert.Equal("Phủ tốt.", matrix.Summary);
    }

    [Fact]
    public void TryParse_CodeFenceAndMissingArrays_UsesLenientDefaults()
    {
        var ok = TraceabilityMatrixParser.TryParse("""
            Đây là ma trận:
            ```json
            { "requirements": [ { "title": "Báo cáo tháng", "status": "MISSING" } ] }
            ```
            """, out var matrix);

        Assert.True(ok);
        var row = Assert.Single(matrix!.Requirements);
        Assert.Equal("R-01", row.Code); // thiếu code ⇒ tự đánh theo thứ tự
        Assert.Equal(TraceabilityMatrix.StatusMissing, row.Status); // status không phân biệt hoa thường
        Assert.Empty(row.Stories);
        Assert.Empty(row.CodeFiles);
        Assert.Empty(row.Tests);
        Assert.Empty(matrix.OrphanStories);
    }

    [Fact]
    public void TryParse_UnknownStatus_ClampsToPartial()
    {
        Assert.True(TraceabilityMatrixParser.TryParse(
            """{ "requirements": [ { "title": "X", "status": "đầy đủ" } ] }""", out var matrix));
        Assert.Equal(TraceabilityMatrix.StatusPartial, matrix!.Requirements[0].Status);
    }

    [Theory]
    [InlineData("văn xuôi không có JSON")]
    [InlineData("{ \"requirements\": [] }")]
    [InlineData("{ \"requirements\": [ { \"status\": \"covered\" } ] }")] // dòng không có title bị loại ⇒ rỗng
    [InlineData("{ \"orphanStories\": [] }")]
    public void TryParse_NoUsableRequirements_ReturnsFalse(string content)
    {
        Assert.False(TraceabilityMatrixParser.TryParse(content, out var matrix));
        Assert.Null(matrix);
    }

    [Fact]
    public void SerializeThenDeserialize_RoundTrips()
    {
        Assert.True(TraceabilityMatrixParser.TryParse(
            """{ "requirements": [ { "code": "R-01", "title": "X", "status": "partial", "note": "thiếu test" } ], "summary": "s" }""",
            out var matrix));

        var restored = TraceabilityMatrixParser.Deserialize(TraceabilityMatrixParser.Serialize(matrix!));

        Assert.NotNull(restored);
        Assert.Equal(matrix!.Requirements[0].Title, restored!.Requirements[0].Title);
        Assert.Equal(matrix.Requirements[0].Note, restored.Requirements[0].Note);
        Assert.Equal(matrix.Summary, restored.Summary);
    }

    [Fact]
    public void Deserialize_BrokenJson_ReturnsNull()
    {
        Assert.Null(TraceabilityMatrixParser.Deserialize("{hỏng"));
    }
}
