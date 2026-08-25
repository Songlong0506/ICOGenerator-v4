using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Phân loại tên màn hình thành nhóm menu của bản demo. Hai lô tên này do chính hệ thống gieo ra
// (EntityMapBuilder.ManagedListScreens / ReportMapBuilder.ReportScreens) nên hình dạng tên là chắc
// chắn; các ca "cố ý KHÔNG bắt" ở dưới mới là phần cần khoá lại — bắt nhầm một màn chủ là dạy agent
// bỏ qua cổng này.
public class PocNavGroupsTests
{
    [Theory]
    [InlineData("JobTitle Catalog")]
    [InlineData("PC Level Catalog")]
    [InlineData("Danh mục Chức danh")]
    [InlineData("DANH MUC KY NANG")]
    public void ClassifiesCatalogScreens(string name)
        => Assert.Equal(PocNavGroupKind.Catalog, PocNavGroups.Classify(name));

    [Theory]
    [InlineData("Headcount Report")]
    [InlineData("Báo cáo Nhân sự")]
    [InlineData("Thống kê Tuyển dụng")]
    [InlineData("Turnover Statistics")]
    [InlineData("Workforce Analytics")]
    public void ClassifiesReportScreens(string name)
        => Assert.Equal(PocNavGroupKind.Report, PocNavGroups.Classify(name));

    [Fact]
    public void CatalogWinsOverReport_ForACatalogOfReportTypes()
        => Assert.Equal(PocNavGroupKind.Catalog, PocNavGroups.Classify("Report Type Catalog"));

    [Theory]
    [InlineData("JD Library")]
    [InlineData("JD Creation")]
    [InlineData("HRBP Approval")]
    // Tên TRẦN là tiêu đề nhóm hoặc màn chủ, không phải thành viên của nhóm.
    [InlineData("Reports")]
    [InlineData("Danh mục")]
    [InlineData("Dashboard")]
    [InlineData("Overview")]
    // "dashboard" cố ý KHÔNG nằm trong danh sách từ khoá báo cáo: đây thường là màn CHỦ của một vai.
    [InlineData("Employee Dashboard")]
    [InlineData("")]
    [InlineData(null)]
    public void LeavesOrdinaryScreensUngrouped(string? name)
        => Assert.Equal(PocNavGroupKind.None, PocNavGroups.Classify(name));

    [Fact]
    public void KindsToGroup_OnlyWhenTheBatchIsBigEnough()
    {
        var twoCatalogs = new[] { "JD Library", "Skill Catalog", "Degree Catalog" };
        Assert.Empty(PocNavGroups.KindsToGroup(twoCatalogs));

        var screens = new[]
        {
            "JD Library", "JD Creation",
            "JobTitle Catalog", "Skill Catalog", "Degree Catalog",
            "Headcount Report", "Turnover Report"
        };

        // 3 danh mục ⇒ phải gom; 2 báo cáo ⇒ chưa (gom hai mục lại chỉ bắt người xem bấm thêm một lượt).
        Assert.Equal(new[] { PocNavGroupKind.Catalog }, PocNavGroups.KindsToGroup(screens));
    }

    [Fact]
    public void KindsToGroup_ReturnsBothBatches()
    {
        var screens = new[]
        {
            "JobTitle Catalog", "Skill Catalog", "Degree Catalog",
            "Headcount Report", "Turnover Report", "Thống kê Chi phí"
        };

        Assert.Equal(new[] { PocNavGroupKind.Catalog, PocNavGroupKind.Report }, PocNavGroups.KindsToGroup(screens));
    }
}
