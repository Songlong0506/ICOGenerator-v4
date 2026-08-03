using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Các dòng "Hoàn thành khi: …" là tiêu chí nghiệm thu DUY NHẤT do chính người dùng viết và đã Approve
// (xem Prompts/BusinessAnalyst/product-brief.v3.md). Chúng phải đi được tới spec § 14 rồi tới bộ UAT, nên
// parser này là mắt xích đầu tiên — bóc hụt một câu là mất luôn một điều đã hứa với người dùng.
public class BriefAcceptanceCriteriaTests
{
    private const string Brief = """
        # Quản lý mượn trả sách
        ## Dành cho ai?
        - Thủ thư: ghi mượn/trả.
        - Nhân viên: tra cứu.
        ## Người dùng làm được những gì? (các tính năng chính)
        - Thủ thư ghi lượt mượn sách cho nhân viên, hẹn trả trong 14 ngày.
          Hoàn thành khi: ghi xong thì sách hiện trạng thái "đang mượn" kèm tên người mượn.
        - Thủ thư xác nhận trả sách.
          *Hoàn thành khi: xác nhận xong thì sách trở về trạng thái "có sẵn".*
        - Nhân viên tra cứu sách.
          - Hoàn thành khi: gõ tên sách thì thấy sách còn hay đang được mượn.
        - Thủ thư xem báo cáo tháng.
        ## Quy tắc cần nhớ
        - Mỗi lượt mượn tối đa 14 ngày.
        """;

    [Fact]
    public void Parse_PicksEveryCriterion_WithItsFeature()
    {
        var criteria = BriefAcceptanceCriteria.Parse(Brief);

        Assert.Equal(3, criteria.Count);
        Assert.Equal("Thủ thư ghi lượt mượn sách cho nhân viên, hẹn trả trong 14 ngày.", criteria[0].Feature);
        Assert.StartsWith("ghi xong thì sách hiện trạng thái", criteria[0].Criterion);
        Assert.Equal("Thủ thư xác nhận trả sách.", criteria[1].Feature);
        Assert.Equal("Nhân viên tra cứu sách.", criteria[2].Feature);
    }

    // Model hay bọc dòng này trong dấu nhấn mạnh markdown hoặc viết nó thành bullet con — cả ba dạng
    // trong Brief ở trên đều phải ra cùng một câu sạch.
    [Fact]
    public void Parse_StripsMarkdownEmphasisAndBulletPrefix()
    {
        var criteria = BriefAcceptanceCriteria.Parse(Brief);

        Assert.DoesNotContain(criteria, c => c.Criterion.StartsWith('*') || c.Criterion.EndsWith('*'));
        Assert.DoesNotContain(criteria, c => c.Criterion.StartsWith("- ", StringComparison.Ordinal));
        Assert.Equal("xác nhận xong thì sách trở về trạng thái \"có sẵn\".", criteria[1].Criterion);
    }

    // Một câu nghiệm thu phải thuộc về tính năng NGAY TRÊN nó. Sau một heading mới thì con trỏ tính năng
    // phải được quên đi, nếu không câu đầu mục sau sẽ bị gán nhầm cho bullet cuối của mục trước.
    [Fact]
    public void Parse_HeadingResetsFeaturePointer()
    {
        var brief = """
            ## Dành cho ai?
            - Thủ thư: ghi mượn/trả.
            ## Người dùng làm được những gì?
              Hoàn thành khi: mở app là thấy danh sách.
            """;

        var criteria = BriefAcceptanceCriteria.Parse(brief);

        Assert.Single(criteria);
        Assert.Equal(string.Empty, criteria[0].Feature);
    }

    [Fact]
    public void Parse_BriefWithoutCriteria_ReturnsEmpty()
    {
        Assert.Empty(BriefAcceptanceCriteria.Parse("# App\n## Các màn hình chính\n- Trang chủ"));
        Assert.Empty(BriefAcceptanceCriteria.Parse(null));
    }

    [Fact]
    public void Parse_DropsDuplicateCriteria()
    {
        var brief = """
            - Tính năng A
              Hoàn thành khi: xong thì thấy kết quả.
            - Tính năng B
              Hoàn thành khi: Xong thì thấy kết quả.
            """;

        Assert.Single(BriefAcceptanceCriteria.Parse(brief));
    }

    // Khối prompt phải render sẵn ĐÚNG các dòng mà spec § 14 cần chứa, để model chỉ việc chép — diễn đạt
    // lại là cách nhanh nhất để câu người dùng đã duyệt biến thành một câu khác.
    [Fact]
    public void BuildPromptBlock_RendersAcLinesReadyToCopy()
    {
        var block = BriefAcceptanceCriteria.BuildPromptBlock(BriefAcceptanceCriteria.Parse(Brief));

        Assert.Contains("## 14. Acceptance Criteria", block);
        Assert.Contains("- AC-1 (Thủ thư ghi lượt mượn sách cho nhân viên, hẹn trả trong 14 ngày.): ghi xong thì sách hiện trạng thái", block);
        Assert.Contains("- AC-3 (Nhân viên tra cứu sách.): gõ tên sách", block);
    }

    [Fact]
    public void BuildPromptBlock_NoCriteria_IsEmptySoThePromptIsUnchanged()
    {
        Assert.Equal(string.Empty, BriefAcceptanceCriteria.BuildPromptBlock(BriefAcceptanceCriteria.Parse("# App")));
    }
}
