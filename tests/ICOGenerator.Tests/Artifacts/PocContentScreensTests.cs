using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

/// <summary>
/// Parser màn-hình dùng để tường thuật tiến độ "đã dựng màn hình X" (U1): chỉ đếm SECTION page-view,
/// bỏ qua modal/CRUD/markup khác. Quét chuỗi thuần nên test được không cần browser.
/// </summary>
public class PocContentScreensTests
{
    [Fact]
    public void Extract_PageViewSection_ReturnsDataViewLabel()
    {
        var content = "<section class=\"page-view active\" data-view=\"Danh sách đơn\"><h1>x</h1></section>";
        Assert.Equal(new[] { "Danh sách đơn" }, PocContentScreens.Extract(content));
    }

    [Fact]
    public void Extract_MultipleSections_ReturnsAllInOrder()
    {
        var content =
            "<section class=\"page-view active\" data-view=\"Trang chủ\"></section>" +
            "<section class=\"page-view\" data-view=\"Báo cáo\"></section>";
        Assert.Equal(new[] { "Trang chủ", "Báo cáo" }, PocContentScreens.Extract(content));
    }

    [Fact]
    public void Extract_ModalWithoutPageView_IsIgnored()
    {
        // Modal (không phải page-view) không phải một màn hình ⇒ không tường thuật.
        var content = "<div class=\"modal fade\" id=\"formModal\" data-view=\"Không tính\"></div>";
        Assert.Empty(PocContentScreens.Extract(content));
    }

    [Fact]
    public void Extract_DecodesHtmlEntities_AndTrims()
    {
        var content = "<section class=\"page-view\" data-view=\"Đơn &amp; Kho\"></section>";
        Assert.Equal(new[] { "Đơn & Kho" }, PocContentScreens.Extract(content));
    }

    [Fact]
    public void Extract_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(PocContentScreens.Extract(null));
        Assert.Empty(PocContentScreens.Extract(""));
    }

    [Fact]
    public void Extract_SectionWithoutDataView_IsSkipped()
    {
        var content = "<section class=\"page-view\"><h1>no data-view</h1></section>";
        Assert.Empty(PocContentScreens.Extract(content));
    }
}
