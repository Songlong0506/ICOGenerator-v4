using ICOGenerator.Contracts.Requirements;
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

    // Mẫu số KHÔNG rút theo [KHÔNG ÁP DỤNG]: thước đo phải đứng yên suốt cuộc phỏng vấn, nếu không con số
    // đang chạy tự nhảy mốc ("0/12" → "1/9") mà người dùng không hiểu vì sao.
    [Fact]
    public void Progress_KeepsTotalAsDenominatorAndCountsNotApplicable()
    {
        var items = CoverageMapParser.Parse("""
            - A: [RÕ] x
            - B: [MỘT PHẦN] y
            - C: [KHÔNG ÁP DỤNG] z
            """);

        var progress = CoverageMapParser.Progress(items);

        Assert.Equal(1, progress.Clear);
        Assert.Equal(2, progress.Applicable);
        Assert.Equal(3, progress.Total);
        Assert.Equal(1, progress.NotApplicable);
    }

    // Bất biến "thanh đầy ⇔ nút Write Requirement mở khoá": cổng readiness chỉ đòi mọi dòng ÁP DỤNG lên
    // [RÕ], nên nhóm [KHÔNG ÁP DỤNG] phải tính là đã xong — không thì thanh mãi không đầy trong khi nút
    // đã sáng, đúng kiểu lệch mà cả UI lẫn tài liệu đang khẳng định là không thể xảy ra.
    [Fact]
    public void Percent_FullWhenEveryApplicableGroupIsClear()
    {
        var map = """
            - A: [RÕ] x
            - B: [RÕ] y
            - C: [KHÔNG ÁP DỤNG] z
            """;
        var items = CoverageMapParser.Parse(map);

        Assert.Equal(100, CoverageMapParser.Progress(items).Percent);
        Assert.True(RequirementReadinessGate.Evaluate(map).Ready);
    }

    [Fact]
    public void Percent_ZeroForEmptyMapAndFreshChecklist()
    {
        Assert.Equal(0, CoverageMapParser.Progress(Array.Empty<CoverageMapItem>()).Percent);
        Assert.Equal(0, CoverageMapParser.Progress(CoverageMapParser.Parse("- A: [CHƯA HỎI]")).Percent);
    }
}
