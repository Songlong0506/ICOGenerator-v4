using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

public class SpecBriefParityCheckerTests
{
    private const string Brief = """
        # App nghỉ phép
        ## Các màn hình chính
        - Danh sách đơn nghỉ: xem các đơn đã gửi
        - Tạo đơn nghỉ — nhập ngày và lý do
        - Màn hình Duyệt đơn: quản lý duyệt/từ chối
        """;

    [Fact]
    public void Check_AllScreensCovered_ReturnsNull()
    {
        var spec = """
            ## 6. Screens To Generate
            ### 6.1. Danh sách đơn nghỉ
            ### 6.2. Tạo đơn nghỉ
            ### 6.3. Duyệt đơn
            ## 10. Business Rules
            - BR-1: x
            """;

        Assert.Null(SpecBriefParityChecker.Check(Brief, spec));
    }

    [Fact]
    public void Check_MissingScreen_ReportsIt()
    {
        var spec = """
            ## 6. Screens To Generate
            ### 6.1. Danh sách đơn nghỉ
            ### 6.2. Tạo đơn nghỉ
            """;

        var report = SpecBriefParityChecker.Check(Brief, spec);

        Assert.NotNull(report);
        Assert.Contains("Duyệt đơn", report);
        Assert.DoesNotContain("Danh sách đơn nghỉ\n- Tạo", report);
    }

    [Fact]
    public void Check_BriefWithoutScreenSection_FailsOpen()
    {
        Assert.Null(SpecBriefParityChecker.Check("# Chỉ có mô tả", "## 6. Screens To Generate\n### 6.1. A"));
    }

    [Fact]
    public void Check_UnparsableSpec_FailsOpen()
    {
        Assert.Null(SpecBriefParityChecker.Check(Brief, "spec tự do không đúng format"));
    }

    [Fact]
    public void ParseBriefScreens_TakesNameBeforeSeparator()
    {
        var screens = SpecBriefParityChecker.ParseBriefScreens(Brief);

        Assert.Equal(3, screens.Count);
        Assert.Equal("Danh sách đơn nghỉ", screens[0]);
        Assert.Equal("Tạo đơn nghỉ", screens[1]);
        Assert.Equal("Màn hình Duyệt đơn", screens[2]);
    }
}
