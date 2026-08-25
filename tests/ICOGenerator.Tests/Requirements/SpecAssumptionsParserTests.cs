using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bóc mục "## 12. Assumptions" của AI Design Spec. Cổng xác nhận giả định bật/tắt theo đúng con số lớp
// này trả về, nên bộ test chia hai vế: (1) NHẬN các kiểu trình bày lệch mà model hay viết ra — heading
// lệch cấp, heading in đậm không dấu '#', danh sách đánh số, tiểu mục bên trong mục — vì không nhận là
// cổng tắt im lặng và giả định đi thẳng vào POC; (2) KHÔNG nhận bừa: dòng in đậm giữa thân bài và bullet
// của mục khác không được kéo vào danh sách.
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

    [Theory]
    [InlineData("- Không có giả định nào")]
    [InlineData("- N/A")]
    [InlineData("- None")]
    [InlineData("- (không có)")]
    public void Parse_PlaceholderVariants_AreSkipped(string bullet)
    {
        Assert.Empty(SpecAssumptionsParser.Parse($"## 12. Assumptions\n{bullet}"));
    }

    [Fact]
    public void Parse_KhongCoOpeningARealAssumption_IsKept()
    {
        // "Không có" đứng đầu một câu THẬT không phải placeholder — cắt nhầm là mất một giả định.
        var spec = "## 12. Assumptions\n- Không có nhân viên external nào được gán JD";

        Assert.Single(SpecAssumptionsParser.Parse(spec));
    }

    [Fact]
    public void Parse_VietnameseHeading_IsRecognized()
    {
        var spec = "## 12. Giả định\n- Chỉ dùng nội bộ phòng HR";

        Assert.Single(SpecAssumptionsParser.Parse(spec));
    }

    [Fact]
    public void Parse_HeadingAtAnyLevel_IsRecognized()
    {
        // Model tụt cấp heading là kiểu lệch phổ biến nhất; trước đây chỉ '##' được nhận.
        Assert.Single(SpecAssumptionsParser.Parse("### 12. Assumptions\n- Mỗi JD có một người tạo"));
        Assert.Single(SpecAssumptionsParser.Parse("#### Giả định\n- Mỗi JD có một người tạo"));
    }

    [Fact]
    public void Parse_NumberedList_IsRecognized()
    {
        var spec = """
            ## 12. Assumptions
            1. Mỗi JD có một OrgUnit áp dụng
            2) Reject không cần nhập lý do
            (3) Mỗi nhân viên chỉ có một assignment Active
            ## 13. Worked Examples
            1. WE-1: không phải giả định
            """;

        var items = SpecAssumptionsParser.Parse(spec);

        Assert.Equal(3, items.Count);
        Assert.Equal("Mỗi JD có một OrgUnit áp dụng", items[0]);
        Assert.Equal("Reject không cần nhập lý do", items[1]);
    }

    [Fact]
    public void Parse_BoldHeadingWithSectionNumber_IsRecognized()
    {
        // Model bỏ hẳn dấu '#' và in đậm dòng tiêu đề — mục kế tiếp (cùng kiểu) phải đóng được mục này.
        var spec = """
            **12. Assumptions**
            - Email chỉ mô phỏng bằng log
            **13. Worked Examples**
            - WE-1: không phải giả định
            """;

        var items = SpecAssumptionsParser.Parse(spec);

        Assert.Single(items);
        Assert.Equal("Email chỉ mô phỏng bằng log", items[0]);
    }

    [Fact]
    public void Parse_SubHeadingsInsideSection_DoNotCloseIt()
    {
        var spec = """
            ## 12. Assumptions
            ### 12.1. Về quy trình duyệt
            - Submit lại sau khi bị từ chối thì quay lại bước HRBP
            ### 12.2. Về dữ liệu
            - COMPAS được mô phỏng bằng dữ liệu mẫu
            ## 13. Worked Examples
            - WE-1: không phải giả định
            """;

        var items = SpecAssumptionsParser.Parse(spec);

        Assert.Equal(2, items.Count);
        Assert.Equal("COMPAS được mô phỏng bằng dữ liệu mẫu", items[1]);
    }

    [Fact]
    public void Parse_BoldLineWithoutSectionNumber_IsNotAHeading()
    {
        // Vế chặt: một dòng in đậm giữa mục khác mà bị coi là heading thì kéo theo mọi bullet phía sau.
        var spec = """
            ## 10. Business Rules
            **Giả định chung: người dùng đã đăng nhập**
            - BR-1: tổng trọng số = 100%
            - BR-2: đơn đã duyệt thì khoá sửa
            """;

        Assert.Empty(SpecAssumptionsParser.Parse(spec));
    }

    [Fact]
    public void Parse_StopsAtMaxItems()
    {
        var spec = "## 12. Assumptions\n" + string.Join('\n', Enumerable.Range(1, 40).Select(i => $"- Giả định {i}"));

        Assert.Equal(30, SpecAssumptionsParser.Parse(spec).Count);
    }
}
