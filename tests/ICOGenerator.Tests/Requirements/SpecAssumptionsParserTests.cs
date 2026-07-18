using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

public class SpecAssumptionsParserTests
{
    [Fact]
    public void Parse_NoSection_ReturnsEmpty()
    {
        Assert.Empty(SpecAssumptionsParser.Parse(null));
        Assert.Empty(SpecAssumptionsParser.Parse("# Spec\n## 6. Screens To Generate\n### 6.1. Home"));
    }

    [Fact]
    public void Parse_ReadsBulletsOfAssumptionsSectionOnly()
    {
        var spec = """
            ## 10. Business Rules
            - BR-1: tổng trọng số = 100%
            ## 12. Assumptions
            - Mỗi nhân viên chỉ thuộc một phòng ban
            - Đơn đã duyệt thì không sửa được nữa
            ## 13. Khác
            - không phải giả định
            """;

        var items = SpecAssumptionsParser.Parse(spec);

        Assert.Equal(2, items.Count);
        Assert.Equal("Mỗi nhân viên chỉ thuộc một phòng ban", items[0]);
    }

    [Fact]
    public void Parse_PlaceholderKhongCo_IsSkipped()
    {
        var spec = "## 12. Assumptions\n- Không có";

        Assert.Empty(SpecAssumptionsParser.Parse(spec));
    }

    [Fact]
    public void Parse_VietnameseHeading_IsRecognized()
    {
        var spec = "## 12. Giả định\n- Chỉ dùng nội bộ phòng HR";

        Assert.Single(SpecAssumptionsParser.Parse(spec));
    }
}
