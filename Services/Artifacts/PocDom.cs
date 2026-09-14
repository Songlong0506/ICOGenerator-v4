using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Phân tích <c>poc-demo.html</c> thành DOM để các tầng kiểm tra tĩnh truy vấn bằng CSS selector.
/// <para>
/// Vì sao không quét chuỗi nữa: lập luận cũ là "markup do máy sinh nên không cần parser HTML". Nửa đầu
/// đúng — khung vỏ do <see cref="PocTemplate"/> dựng — nhưng phần NỘI DUNG bên trong khung là do model
/// viết qua tool, và đó đúng là loại markup làm regex gãy: đổi thứ tự thuộc tính
/// (<c>&lt;div data-roles="X" class="nav-item"&gt;</c> không khớp mẫu <c>&lt;div class="nav-item</c>),
/// xuống dòng giữa thẻ, nháy đơn thay nháy kép, dấu <c>&gt;</c> nằm trong giá trị thuộc tính. Và khi
/// regex gãy ở đây nó KHÔNG báo lỗi — nó báo "audit OK", đúng cái chế độ hỏng mà cả PocAudit sinh ra để
/// chặn.
/// </para>
/// <para>
/// Bỏ luôn được ba thứ tự chế: bước xóa <c>&lt;!-- --&gt;</c>/<c>&lt;style&gt;</c>/<c>&lt;script&gt;</c>
/// bằng regex trước khi quét (thứ tự xóa từng là một cái bẫy có thật — một comment CSS nhắc tới chữ
/// "&lt;script&gt;" làm mẫu script nuốt trọn phần còn lại của trang), hàm <c>MatchingDivEnd</c> đếm độ sâu
/// <c>&lt;div&gt;</c> để tìm thẻ đóng, và <c>BlockAfter</c> cắt chuỗi tới thẻ đóng gần nhất. Trong DOM,
/// nội dung script/style là VĂN BẢN chứ không phải phần tử nên không selector nào chạm tới, comment là
/// node riêng, và phạm vi con của một phần tử là cây con của chính nó.
/// </para>
/// <para>
/// AngleSharp phục hồi lỗi markup theo đúng luật của trình duyệt, nên cây ở đây khớp với cây mà
/// <see cref="PocRuntimeChecker"/> lái bằng Chromium — hai tầng kiểm cùng nhìn một thứ.
/// </para>
/// </summary>
public static class PocDom
{
    private static readonly HtmlParser Parser = new();

    public static IHtmlDocument Parse(string? html) => Parser.ParseDocument(html ?? string.Empty);

    /// <summary>
    /// Chữ NGƯỜI DÙNG NHÌN THẤY trong một ĐOẠN markup: nội dung các node văn bản, nối bằng KHOẢNG TRẮNG,
    /// bỏ qua <c>&lt;script&gt;</c>/<c>&lt;style&gt;</c> và mọi giá trị thuộc tính.
    /// <para>
    /// Không dùng thẳng <c>TextContent</c>: theo đặc tả DOM nó KỂ CẢ nội dung script/style — tức đem tên
    /// biến JavaScript đi đếm dấu tiếng Việt — và nó nối văn bản của hai phần tử liền nhau mà không có
    /// dấu phân cách, làm mọi phép dò biên từ trượt.
    /// </para>
    /// <para>
    /// Parse theo NGỮ CẢNH <c>&lt;tbody&gt;</c> chứ không phải một tài liệu rời. Đây là đoạn markup cắt ra
    /// từ giữa trang, và model hoàn toàn có thể trả về một chùm <c>&lt;td&gt;</c>/<c>&lt;tr&gt;</c> trần.
    /// Parse chúng như một tài liệu thì trình phân tích LOẠI BỎ các thẻ đó theo đúng luật trình duyệt, và
    /// hai đoạn text hai bên bị GỘP thành một node — <c>"…Văn A"</c> + <c>"Sản phẩm B"</c> thành
    /// <c>"Văn ASản phẩm B"</c>, ranh giới đã mất thì không node-walk nào lấy lại được, và mọi mẫu dò dữ
    /// liệu mẫu giả (vốn neo vào biên từ) trượt hết. Ngữ cảnh bảng giữ nguyên cả hai loại đoạn: chùm ô
    /// trần lẫn markup bình thường (<c>&lt;div&gt;</c>, <c>&lt;section&gt;</c>, bảng đầy đủ).
    /// </para>
    /// </summary>
    public static string VisibleText(string? markup)
    {
        if (string.IsNullOrEmpty(markup))
            return string.Empty;

        var host = Parse("<table><tbody></tbody></table>").QuerySelector("tbody");
        if (host == null)
            return string.Empty;

        var text = new StringBuilder();
        foreach (var node in Parser.ParseFragment(markup, host))
            Collect(node, text);

        return text.ToString().Trim();
    }

    /// <summary>
    /// Gom chữ của MỘT node và cây con của nó. Phép xét script/style phải nằm ở ĐẦU hàm, áp cho chính
    /// node được truyền vào — nếu chỉ xét khi duyệt con thì một <c>&lt;script&gt;</c> nằm ở cấp cao nhất
    /// của đoạn markup đi thẳng vào kết quả.
    /// </summary>
    private static void Collect(INode node, StringBuilder text)
    {
        switch (node)
        {
            case IText t:
                text.Append(t.Data).Append(' ');
                return;

            // Nội dung của hai thẻ này là mã, không phải chữ hiển thị.
            case IElement e when e.LocalName is "script" or "style":
                return;
        }

        foreach (var child in node.ChildNodes)
            Collect(child, text);
    }
}
