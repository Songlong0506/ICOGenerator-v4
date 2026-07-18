using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

public class CoverageMapParserTests
{
    [Fact]
    public void Parse_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(CoverageMapParser.Parse(null));
        Assert.Empty(CoverageMapParser.Parse("   "));
    }

    [Fact]
    public void Parse_StandardMap_ReadsStatusCoreAndSummary()
    {
        var map = """
            - ★ Mục tiêu / bài toán: [RÕ] Quản lý đơn nghỉ phép
            - ★ Đối tượng người dùng & vai trò: [MỘT PHẦN] Nhân viên + quản lý; còn thiếu: admin?
            - Báo cáo / thống kê: [CHƯA HỎI]
            - Phân quyền theo nghiệp vụ: [KHÔNG ÁP DỤNG] ứng dụng cá nhân
            """;

        var items = CoverageMapParser.Parse(map);

        Assert.Equal(4, items.Count);
        Assert.True(items[0].IsCore);
        Assert.Equal("Mục tiêu / bài toán", items[0].Label);
        Assert.Equal("RÕ", items[0].Status);
        Assert.Equal("Quản lý đơn nghỉ phép", items[0].Summary);
        Assert.Equal("MỘT PHẦN", items[1].Status);
        Assert.False(items[2].IsCore);
        Assert.Equal("CHƯA HỎI", items[2].Status);
        Assert.Equal("KHÔNG ÁP DỤNG", items[3].Status);
    }

    [Fact]
    public void Parse_IgnoresProseLinesAroundMap()
    {
        var map = "Đây là bản đồ:\n- ★ Mục tiêu / bài toán: [RÕ] ok\nHết.";

        var items = CoverageMapParser.Parse(map);

        Assert.Single(items);
    }

    [Fact]
    public void Progress_ExcludesNotApplicableFromDenominator()
    {
        var items = CoverageMapParser.Parse("""
            - A: [RÕ] x
            - B: [MỘT PHẦN] y
            - C: [KHÔNG ÁP DỤNG] z
            """);

        var (clear, applicable) = CoverageMapParser.Progress(items);

        Assert.Equal(1, clear);
        Assert.Equal(2, applicable);
    }
}
