using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Parser của vòng tự soát Product Brief. Các test chốt: (1) đọc JSON {"issues":[...]} kể cả khi bọc trong
// code fence; (2) mọi đường hỏng (rỗng / văn xuôi / JSON vỡ) đều fail-open về "không có vấn đề" — vòng tự
// soát không bao giờ được chặn việc sinh tài liệu; (3) làm sạch: bỏ mục rỗng/trùng/quá dài, chặn trên số
// lượng; (4) Normalize áp cùng giới hạn cho đường structured output.
public class ProductBriefReviewParserTests
{
    private readonly ProductBriefReviewParser _parser = new();

    [Fact]
    public void Parse_ValidJson_ReturnsIssues()
    {
        var review = _parser.Parse("""
            ```json
            { "issues": ["Thiếu yêu cầu xuất Excel người dùng đã nêu", "Màn hình Thống kê không có trong hội thoại"] }
            ```
            """);

        Assert.Equal(2, review.Issues.Count);
        Assert.Equal("Thiếu yêu cầu xuất Excel người dùng đã nêu", review.Issues[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bản nháp nhìn chung ổn, không có vấn đề gì lớn.")]
    [InlineData("{ \"issues\": [\"mất ngoặc\"")]
    public void Parse_Unparseable_FailsOpenToNoIssues(string raw)
    {
        Assert.Empty(_parser.Parse(raw).Issues);
    }

    [Fact]
    public void Parse_CleansIssues_DropsBlankDuplicateOverlong_AndCaps()
    {
        var overlong = new string('x', 501);
        var many = string.Join(",", Enumerable.Range(1, 12).Select(i => $"\"Vấn đề {i}\""));
        var review = _parser.Parse(
            $$"""{ "issues": ["", "  ", "Trùng", "Trùng", "{{overlong}}", {{many}}] }""");

        Assert.Equal(8, review.Issues.Count); // chặn trên MaxIssues
        Assert.Equal("Trùng", review.Issues[0]);
        Assert.DoesNotContain(overlong, review.Issues);
    }

    [Fact]
    public void Normalize_AppliesSameLimitsToStructuredOutput()
    {
        var structured = new ProductBriefReview
        {
            Issues = Enumerable.Range(1, 12).Select(i => $"Vấn đề {i}").Append("Vấn đề 1").ToList()
        };

        var review = _parser.Normalize(structured);

        Assert.Equal(8, review.Issues.Count);
        Assert.Equal("Vấn đề 1", review.Issues[0]);
    }
}
