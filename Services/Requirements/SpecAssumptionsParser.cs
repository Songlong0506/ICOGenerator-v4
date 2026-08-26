using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Một giả định bóc từ mục "## 12. Assumptions". <paramref name="Text"/> đã BỎ nhãn phân loại — nó là
/// chuỗi đi vào trí nhớ giả định (<see cref="AssumptionMemory"/>) và hiện trên cổng, nên phải giữ đúng
/// hình dạng như trước khi có nhãn: đổi nó là mọi giả định user đã duyệt trước đây thành "mới" và bị hỏi
/// lại một lượt.
/// </summary>
/// <param name="IsSimulation">
/// Giả định thuộc nhóm MÔ PHỎNG: quyết định về việc bản demo giả lập hạ tầng thật thế nào (đăng nhập,
/// đồng bộ hệ thống ngoài, gửi email, định dạng file xuất). Người dùng nghiệp vụ KHÔNG được hỏi những
/// thứ này — <c>requirement-chat.v4.md</c> cấm BA hỏi chúng ngay từ buổi phỏng vấn — nên cổng chỉ HIỆN
/// chúng để biết, không bắt trả lời. Nhóm còn lại (nghiệp vụ) mới là phần cổng thật sự hỏi.
/// </param>
public sealed record SpecAssumption(string Text, bool IsSimulation);

/// <summary>
/// Bóc mục "## 12. Assumptions" (các giả định BA tự đưa khi sinh AI Design Spec) ra khỏi spec markdown
/// để hiển thị cho người dùng thường. Lý do tồn tại: spec được phép "tự đưa giả định hợp lý" rồi đi
/// thẳng vào bước dựng POC không qua mắt người dùng — panel giả định là chỗ duy nhất user thấy các
/// quyết định thay mặt mình TRƯỚC khi POC hiện ra "lạ lạ". Chịu lỗi: không có mục → danh sách rỗng.
///
/// CHỊU ĐỊNH DẠNG LỆCH là yêu cầu chính của lớp này, không phải tiện ích thêm. Cổng xác nhận giả định
/// bật/tắt theo đúng con số lớp này trả về (xem <c>AgentTaskWorker</c>), nên mọi kiểu trình bày mà lớp
/// này không nhận đều biến thành "spec không có giả định nào" — cổng TẮT IM LẶNG trong khi các giả định
/// vẫn nằm trong spec và vẫn lái POC. Đó là hỏng nguy hiểm hơn hẳn chiều ngược lại (hỏi thừa một dòng),
/// nên khi lưỡng lự thì nhận. Ba kiểu lệch thật đã gặp giữa các model:
/// <list type="bullet">
///   <item>heading lệch cấp — <c>### 12. Assumptions</c> thay vì <c>##</c>, hoặc bỏ '#' và in đậm cả
///   dòng (<c>**12. Assumptions**</c>);</item>
///   <item>tiểu mục bên trong mục giả định (<c>### 12.1. …</c>) — heading SÂU HƠN heading đã mở mục thì
///   không đóng mục, nếu không mọi bullet dưới tiểu mục đầu tiên rơi hết;</item>
///   <item>danh sách đánh số (<c>1. …</c>, <c>1) …</c>) thay vì bullet gạch đầu dòng.</item>
/// </list>
/// Chiều ngược lại vẫn phải chặt: một dòng in đậm giữa thân bài chỉ được coi là heading khi nó mang SỐ
/// MỤC (<c>**12. …**</c>), vì "<c>**Giả định chung: …**</c>" nằm trong mục Business Rules mà bị hiểu
/// thành heading thì kéo theo toàn bộ bullet phía sau vào danh sách giả định.
/// </summary>
public static partial class SpecAssumptionsParser
{
    private const int MaxItems = 30;

    // Heading in đậm (không có dấu '#') xếp cấp sâu nhất: mục do heading '#' thật mở ra thì một dòng in
    // đậm bên trong không đóng được, còn mục do chính một dòng in đậm mở ra thì heading '#' ở bất kỳ cấp
    // nào — và dòng in đậm kế tiếp — đều đóng được.
    private const int BoldHeadingLevel = 6;

    public static IReadOnlyList<SpecAssumption> Parse(string? specMarkdown)
    {
        if (string.IsNullOrWhiteSpace(specMarkdown))
            return Array.Empty<SpecAssumption>();

        var items = new List<SpecAssumption>();
        // 0 = đang ở ngoài mục giả định; > 0 = cấp của heading đã mở mục.
        var sectionLevel = 0;

        foreach (var raw in specMarkdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (TryReadHeading(line, out var level, out var headingText))
            {
                // Heading sâu hơn heading đã mở mục = tiểu mục BÊN TRONG mục giả định, không phải mục kế.
                if (sectionLevel > 0 && level > sectionLevel)
                    continue;

                sectionLevel = IsAssumptionHeading(headingText) ? level : 0;
                continue;
            }

            if (sectionLevel == 0)
                continue;

            var bullet = BulletRegex().Match(line);
            if (!bullet.Success)
                continue;

            var value = bullet.Groups[1].Value.Replace("**", string.Empty).Trim();

            // Nhãn phân loại ở đầu bullet ("- [NGHIỆP VỤ] …" / "- [MÔ PHỎNG] …"). Nhãn lạ hoặc thiếu nhãn
            // ⇒ coi là NGHIỆP VỤ: hỏi thừa một dòng thì mất vài giây, còn xếp nhầm một quyết định nghiệp
            // vụ vào nhóm "chỉ để biết" là tự quyết thay người dùng đúng kiểu mà cổng sinh ra để chặn.
            // Chỉ CẮT nhãn khi nhận ra nó là nhãn phân loại: một giả định mở đầu bằng ngoặc vuông của
            // chính nội dung ("[Xuất báo cáo] dùng định dạng CSV") mà bị cắt thì câu hiện lên cụt nghĩa.
            var simulation = false;
            var tag = TagRegex().Match(value);
            if (tag.Success && IsClassificationTag(tag.Groups[1].Value))
            {
                simulation = IsSimulationTag(tag.Groups[1].Value);
                value = value[tag.Length..].Trim();
            }

            // Placeholder "Không có" nghĩa là spec không có giả định nào — đừng hiển thị nó như một giả định.
            if (value.Length == 0 || PlaceholderRegex().IsMatch(value))
                continue;

            items.Add(new SpecAssumption(value, simulation));
            if (items.Count >= MaxItems)
                break;
        }

        return items;
    }

    private static bool TryReadHeading(string line, out int level, out string text)
    {
        var hash = HashHeadingRegex().Match(line);
        if (hash.Success)
        {
            level = hash.Groups[1].Value.Length;
            text = hash.Groups[2].Value.Trim();
            return true;
        }

        var bold = BoldHeadingRegex().Match(line);
        if (bold.Success)
        {
            level = BoldHeadingLevel;
            text = bold.Groups[1].Value.Trim();
            return true;
        }

        level = 0;
        text = string.Empty;
        return false;
    }

    private static bool IsClassificationTag(string tag) =>
        IsSimulationTag(tag)
        || tag.Contains("nghiệp vụ", StringComparison.OrdinalIgnoreCase)
        || tag.Contains("nghiep vu", StringComparison.OrdinalIgnoreCase)
        || tag.Contains("business", StringComparison.OrdinalIgnoreCase);

    // Nhận cả biến thể tiếng Anh và bản viết không dấu — nhãn là thứ model tự gõ lại, không phải hằng số.
    private static bool IsSimulationTag(string tag) =>
        tag.Contains("mô phỏng", StringComparison.OrdinalIgnoreCase)
        || tag.Contains("mo phong", StringComparison.OrdinalIgnoreCase)
        || tag.Contains("simulat", StringComparison.OrdinalIgnoreCase)
        || tag.Contains("mock", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssumptionHeading(string headingText) =>
        headingText.Contains("assumption", StringComparison.OrdinalIgnoreCase)
        || headingText.Contains("giả định", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(#{1,6})\s*(.+?)\s*#*$")]
    private static partial Regex HashHeadingRegex();

    // "**12. Assumptions**", "**## 12. Giả định**" — SỐ MỤC là bắt buộc, xem ghi chú lớp.
    [GeneratedRegex(@"^\*\*\s*#{0,6}\s*\d+(?:\.\d+)*[.)]?\s*(.+?)\s*\*\*:?$")]
    private static partial Regex BoldHeadingRegex();

    // Gạch đầu dòng mọi kiểu + danh sách đánh số ("1.", "1)", "(1)").
    [GeneratedRegex(@"^(?:[-*+•‣▪–—]|\(?\d+[.)])\s+(.+)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^\[([^\]]{1,40})\]\s*")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^\(?(?:không(?:\s+có)?(?:\s+giả định.*)?|no(?:ne|\s+assumptions?)?|n/?a)\)?\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderRegex();
}
