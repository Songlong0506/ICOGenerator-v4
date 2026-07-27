using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Tầng kiểm DỮ LIỆU MẪU + NGÔN NGỮ của POC. Ba bất biến đáng giữ: (1) POC phải dùng chính danh mục của
// tài liệu người dùng gửi khi có tài liệu; (2) placeholder kinh điển bị bắt, nhưng NẶNG hơn khi đã có dữ
// liệu thật để dùng; (3) spec tiếng Việt mà UI không có lấy một dấu là ISSUE. Và quan trọng nhất: không
// có bối cảnh nào thì phép kiểm phải im lặng tuyệt đối (không đổi hành vi audit cũ).
public class PocSampleDataCheckTests
{
    private const string RealSample = """
        [Trích từ danh-muc.xlsx]
        Mã vật tư | Tên vật tư | Đơn vị tính
        VT-0091 | Vòng bi trục khuỷu SKF | Chiếc
        VT-0092 | Dầu thủy lực Shell Tellus | Thùng
        VT-0093 | Gioăng làm kín Viton | Bộ
        """;

    [Fact]
    public void Inspect_WithoutContext_ReportsNothing()
    {
        var result = PocSampleDataCheck.Inspect(Poc("<p>Nguyễn Văn A</p>"), null);

        Assert.Empty(result.Issues);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Inspect_DemoIgnoresTheAttachedCatalogue_IsAnIssue()
    {
        var html = Poc("<table><tr><td>Sản phẩm thứ nhất</td><td>Kho trung tâm</td></tr></table>");

        var result = PocSampleDataCheck.Inspect(html, new PocSampleDataContext(null, RealSample));

        Assert.Contains(result.Issues, i => i.Contains("KHÔNG dùng gì từ tài liệu"));
        // Gợi ý phải nêu đúng giá trị thật để agent biết seed bằng gì.
        Assert.Contains(result.Issues, i => i.Contains("Vòng bi trục khuỷu SKF"));
    }

    [Fact]
    public void Inspect_DemoSeededFromTheCatalogue_IsClean()
    {
        var html = Poc("""
            <table>
              <tr><td>Vòng bi trục khuỷu SKF</td><td>Chiếc</td></tr>
              <tr><td>Dầu thủy lực Shell Tellus</td><td>Thùng</td></tr>
              <tr><td>Gioăng làm kín Viton</td><td>Bộ</td></tr>
            </table>
            """);

        var result = PocSampleDataCheck.Inspect(html, new PocSampleDataContext(null, RealSample));

        Assert.Empty(result.Issues);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Inspect_PartialUseOfTheCatalogue_IsOnlyAWarning()
    {
        // Một giá trị thật + phần còn lại tự nghĩ: chưa tới mức chặn, nhưng đáng nhắc.
        var html = Poc("<table><tr><td>Vòng bi trục khuỷu SKF</td><td>Hàng tồn khác</td></tr></table>");

        var result = PocSampleDataCheck.Inspect(html, new PocSampleDataContext(null, RealSample));

        Assert.Empty(result.Issues);
        Assert.Contains(result.Warnings, w => w.Contains("chỉ dùng 1 giá trị"));
    }

    [Fact]
    public void Inspect_PlaceholderNames_AreAWarningWhenNoDocumentWasAttached()
    {
        var html = Poc("<td>Nguyễn Văn A</td><td>Sản phẩm B</td>");

        var result = PocSampleDataCheck.Inspect(html, new PocSampleDataContext(null, null));

        Assert.Empty(result.Issues);
        Assert.Contains(result.Warnings, w => w.Contains("placeholder"));
    }

    [Fact]
    public void Inspect_PlaceholderNames_BecomeAnIssueWhenRealDataWasAvailable()
    {
        var html = Poc("<td>Nguyễn Văn A</td><td>Vòng bi trục khuỷu SKF</td><td>Dầu thủy lực Shell Tellus</td><td>Gioăng làm kín Viton</td>");

        var result = PocSampleDataCheck.Inspect(html, new PocSampleDataContext(null, RealSample));

        Assert.Contains(result.Issues, i => i.Contains("tên bịa"));
    }

    [Fact]
    public void Inspect_VietnameseSpecWithAnEnglishDemo_IsAnIssue()
    {
        var spec = "## 6. Screens To Generate\n### Đăng nhập\nNgười dùng đăng nhập bằng tài khoản nội bộ rồi xem danh sách đơn hàng của phòng mình.";
        var html = Poc("<h1>Order list</h1><table><tr><th>Code</th><th>Status</th></tr></table>");

        var result = PocSampleDataCheck.Inspect(html, new PocSampleDataContext(spec, null));

        Assert.Contains(result.Issues, i => i.Contains("TIẾNG VIỆT"));
    }

    [Fact]
    public void Inspect_VietnameseSpecWithVietnameseDemo_IsClean()
    {
        var spec = "## 6. Screens To Generate\n### Đăng nhập\nNgười dùng đăng nhập bằng tài khoản nội bộ rồi xem danh sách đơn hàng của phòng mình.";
        var html = Poc("<h1>Danh sách đơn hàng</h1><table><tr><th>Mã đơn</th><th>Trạng thái</th></tr></table>");

        var result = PocSampleDataCheck.Inspect(html, new PocSampleDataContext(spec, null));

        Assert.Empty(result.Issues);
    }

    // Nhãn nằm trong ATTRIBUTE không chứng minh chữ HIỂN THỊ là tiếng Việt — người dùng không đọc
    // data-view. Đây là chỗ một phép kiểm ngây thơ (quét cả markup) sẽ pass nhầm.
    [Fact]
    public void Inspect_VietnameseOnlyInAttributes_StillCountsAsAnEnglishUi()
    {
        var spec = "Người dùng đăng nhập rồi xem danh sách đơn hàng của phòng mình, quản lý duyệt từng đơn.";
        var html = Poc("<section class=\"page-view\" data-view=\"Đơn hàng của tôi\"><h1>My orders</h1></section>");

        var result = PocSampleDataCheck.Inspect(html, new PocSampleDataContext(spec, null));

        Assert.Contains(result.Issues, i => i.Contains("TIẾNG VIỆT"));
    }

    [Fact]
    public void Inspect_ShellOutsideTheContentRegion_IsNotScanned()
    {
        // Chữ ngoài vùng POC_CONTENT là của shell template (sidebar/topbar/popup) — quét nó là chấm
        // template chứ không phải bản demo được sinh ra.
        var html = "<body><nav>Lorem ipsum John Doe</nav>"
                   + PocTemplate.StartMarker + "<h1>Danh sách đơn hàng của phòng Kỹ thuật</h1>" + PocTemplate.EndMarker
                   + "</body>";

        var result = PocSampleDataCheck.Inspect(html, new PocSampleDataContext(null, null));

        Assert.Empty(result.Issues);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ExtractTokens_KeepsDistinctiveValuesAndDropsExtractorNoise()
    {
        var tokens = PocSampleDataCheck.ExtractTokens(RealSample);

        Assert.Contains("Vòng bi trục khuỷu SKF", tokens);
        // Dòng chú thích của trình bóc text không phải dữ liệu nghiệp vụ.
        Assert.DoesNotContain(tokens, t => t.StartsWith('['));
        // Token quá ngắn ("Bộ", "Chiếc") có mặt trong mọi POC tiếng Việt nên không chứng minh được gì.
        Assert.DoesNotContain("Bộ", tokens);
    }

    private static string Poc(string content) =>
        "<html><body><nav class=\"sidebar-nav\"></nav>"
        + PocTemplate.StartMarker + content + PocTemplate.EndMarker
        + "</body></html>";
}
