using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Đường Product Brief của RequirementResponseParser trong chế độ "không giả định". Các test chốt:
// (1) parse được van thoát needsClarification (kèm câu hỏi + gợi ý) từ JSON, kể cả khác hoa/thường;
// (2) Normalize null-safe cho các trường mới và làm sạch gợi ý (bỏ rỗng/trùng/quá dài, chặn trên) —
// dùng chung cho đường structured output; (3) fallback template khi JSON hỏng KHÔNG còn mục
// "Điểm cần xác nhận" (tài liệu không được chứa giả định); (4) TryParseProductBrief (vòng sửa) giữ
// nguyên cờ NeedsClarification để service loại bản sửa "trả bóng" sai chỗ.
public class RequirementResponseParserTests
{
    private readonly RequirementResponseParser _parser = new();
    private readonly Project _project = new() { Id = Guid.NewGuid(), Name = "App kho" };

    [Fact]
    public void ParseProductBrief_NeedsClarification_ReadsQuestionAndSuggestions()
    {
        var result = _parser.ParseProductBrief("""
            ```json
            {
              "assistantMessage": "Cần làm rõ trước khi viết.",
              "productBrief": { "content": "" },
              "NeedsClarification": true,
              "clarifyingQuestion": "HOD có phải cấp trên trực tiếp của Manager không?",
              "clarifyingSuggestions": ["Đúng vậy", "Không phải"]
            }
            ```
            """, _project, "user message");

        Assert.True(result.NeedsClarification);
        Assert.Equal("HOD có phải cấp trên trực tiếp của Manager không?", result.ClarifyingQuestion);
        Assert.Equal(new[] { "Đúng vậy", "Không phải" }, result.ClarifyingSuggestions);
    }

    [Fact]
    public void ParseProductBrief_NormalDraft_FlagDefaultsToFalse()
    {
        var result = _parser.ParseProductBrief(
            """{ "assistantMessage": "Đã tạo.", "productBrief": { "content": "# App kho" } }""",
            _project, "user message");

        Assert.False(result.NeedsClarification);
        Assert.Equal("", result.ClarifyingQuestion);
        Assert.Empty(result.ClarifyingSuggestions);
        Assert.Equal("# App kho", result.ProductBrief.Content);
    }

    [Fact]
    public void ParseProductBrief_BrokenJson_FallbackTemplateHasNoAssumptionSection()
    {
        var result = _parser.ParseProductBrief("{ \"assistantMessage\": \"mất ngoặc\"", _project, "quản lý kho");

        Assert.False(result.NeedsClarification);
        Assert.DoesNotContain("Điểm cần xác nhận", result.ProductBrief.Content);
        Assert.Contains("quản lý kho", result.ProductBrief.Content);
    }

    [Fact]
    public void Normalize_NullFields_BecomeNonNull()
    {
        var result = _parser.Normalize(new BAProductBriefResult
        {
            ProductBrief = null!,
            ClarifyingQuestion = null!,
            ClarifyingSuggestions = null!
        });

        Assert.NotNull(result.ProductBrief);
        Assert.Equal("", result.ProductBrief.Content);
        Assert.Equal("", result.ClarifyingQuestion);
        Assert.NotNull(result.ClarifyingSuggestions);
        Assert.Empty(result.ClarifyingSuggestions);
    }

    [Fact]
    public void Normalize_CleansClarifyingSuggestions_DropsBlankDuplicateOverlong_AndCaps()
    {
        var overlong = new string('x', 201);
        var suggestions = new List<string> { "", "  ", "Trùng", " Trùng ", overlong };
        suggestions.AddRange(Enumerable.Range(1, 10).Select(i => $"Phương án {i}"));

        var result = _parser.Normalize(new BAProductBriefResult
        {
            ClarifyingQuestion = "  Câu hỏi?  ",
            ClarifyingSuggestions = suggestions
        });

        Assert.Equal("Câu hỏi?", result.ClarifyingQuestion);
        Assert.Equal(6, result.ClarifyingSuggestions.Count); // chặn trên số chip
        Assert.Equal("Trùng", result.ClarifyingSuggestions[0]);
        Assert.DoesNotContain(overlong, result.ClarifyingSuggestions);
    }

    [Fact]
    public void TryParseProductBrief_KeepsNeedsClarificationFlag_ForRevisionGuard()
    {
        var revised = _parser.TryParseProductBrief("""
            { "assistantMessage": "x", "productBrief": { "content": "# App" }, "needsClarification": true }
            """);

        Assert.NotNull(revised);
        Assert.True(revised!.NeedsClarification);
    }

    [Fact]
    public void TryParseProductBrief_BrokenJson_ReturnsNull()
    {
        Assert.Null(_parser.TryParseProductBrief("văn xuôi thuần, không có JSON"));
    }
}
