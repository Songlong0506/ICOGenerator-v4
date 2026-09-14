using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Lớp bóc chữ hiển thị của POC. Hai cạm bẫy dưới đây đều đã xảy ra thật khi chuyển từ quét regex sang
// DOM, và cả hai đều hỏng LẶNG LẼ (không lỗi, chỉ là phép dò dữ liệu mẫu giả trượt sạch), nên chúng phải
// có test riêng chứ không nằm lẫn trong PocSampleDataCheckTests.
public class PocDomTests
{
    // Hai phần tử LIỀN NHAU phải còn ranh giới từ. TextContent của DOM nối thẳng, ra "Văn ASản phẩm B" —
    // "A" thành một phần của từ kế và mọi mẫu dò neo vào biên từ (Nguyễn Văn A(?![\p{L}])) trượt hết.
    [Fact]
    public void VisibleText_KeepsAWordBoundaryBetweenAdjacentElements()
    {
        var text = PocDom.VisibleText("<td>Nguyễn Văn A</td><td>Sản phẩm B</td>");

        Assert.Contains("Nguyễn Văn A ", text);
        Assert.DoesNotContain("ASản", text);
    }

    // Chùm ô TRẦN (không có <table> bọc) vẫn phải đọc được. Parse như một tài liệu thì trình phân tích
    // loại bỏ các <td> theo luật trình duyệt và GỘP hai đoạn text hai bên làm một — ranh giới mất thì
    // không lấy lại được. Vì thế PocDom parse theo ngữ cảnh <tbody>.
    [Theory]
    [InlineData("<td>Vòng bi SKF</td>")]
    [InlineData("<tr><td>Vòng bi SKF</td></tr>")]
    [InlineData("<table><tr><td>Vòng bi SKF</td></tr></table>")]
    [InlineData("<div><p>Vòng bi SKF</p></div>")]
    [InlineData("<section class=\"page-view\" data-view=\"Kho\">Vòng bi SKF</section>")]
    public void VisibleText_ReadsBothBareTableParts_AndOrdinaryMarkup(string markup)
    {
        Assert.Contains("Vòng bi SKF", PocDom.VisibleText(markup));
    }

    // Nội dung script/style là MÃ, không phải chữ hiển thị. TextContent của DOM kể cả chúng, tức đem tên
    // biến JavaScript đi đếm dấu tiếng Việt và dò tên bịa.
    [Fact]
    public void VisibleText_SkipsScriptAndStyleContent()
    {
        var text = PocDom.VisibleText(
            "<p>Vòng bi</p><script>var nguyenVanA = 'Nguyễn Văn A';</script><style>.x{content:'Sản phẩm B'}</style>");

        Assert.Contains("Vòng bi", text);
        Assert.DoesNotContain("nguyenVanA", text);
        Assert.DoesNotContain("Nguyễn Văn A", text);
        Assert.DoesNotContain("Sản phẩm B", text);
    }

    // Giá trị thuộc tính không phải chữ người dùng nhìn thấy: một data-view="Đăng nhập" không chứng minh
    // nhãn hiển thị là tiếng Việt.
    [Fact]
    public void VisibleText_IgnoresAttributeValues()
    {
        var text = PocDom.VisibleText("<section data-view=\"Đăng nhập\" title=\"Tiếng Việt\">Login</section>");

        Assert.Equal("Login", text);
    }

    // Dấu '>' trong giá trị thuộc tính: mẫu cũ <[^>]*> cắt thẻ ngay tại đó và đẩy phần đuôi của thẻ —
    // tên thuộc tính và tất cả — vào "chữ nhìn thấy".
    [Fact]
    public void VisibleText_HandlesGreaterThanInsideAttributeValue()
    {
        var text = PocDom.VisibleText("<span title=\"doanh thu > 0\">Báo cáo</span>");

        Assert.Equal("Báo cáo", text);
        Assert.DoesNotContain("title", text);
    }
}
