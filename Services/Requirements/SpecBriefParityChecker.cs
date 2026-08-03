using System.Text;
using System.Text.RegularExpressions;
using ICOGenerator.Services.Artifacts;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Đối chiếu deterministic giữa Product Brief đã duyệt và AI Design Spec vừa sinh. Prompt yêu cầu spec
/// "mô tả CÙNG MỘT sản phẩm" với Brief nhưng không gì kiểm chứng, trong khi spec là ĐẦU VÀO DUY NHẤT của
/// bước dựng POC: thứ gì rơi rụng ở biên này thì POC thiếu luôn, và mọi cổng phía sau đều mù vì chúng chỉ
/// so POC với spec. Ba tầng được soát, theo đúng thứ tự "mất mát đắt dần":
/// <list type="number">
///   <item><b>Màn hình</b> — "## Các màn hình chính" của Brief ↔ "§ 6. Screens To Generate".</item>
///   <item><b>Quy tắc nghiệp vụ</b> — "## Quy tắc cần nhớ" của Brief ↔ "§ 10. Business Rules". Rule mới
///   là thứ POC phải CHẠY được; một rule rơi rụng cho ra bản demo bấm được nhưng sai nghiệp vụ.</item>
///   <item><b>Câu nghiệm thu</b> — các dòng "Hoàn thành khi: …" ↔ "§ 14. Acceptance Criteria". Đây là
///   chữ của chính người dùng và là đích của bộ UAT; mất ở đây là mất luôn tiêu chí nghiệm thu.</item>
/// </list>
/// Phát hiện lệch thì caller cho BA sửa lại MỘT vòng (xem <see cref="RequirementDocsService"/>).
/// Fail-open từng tầng: mục nào Brief/spec không có thì tầng đó im lặng, không chặn pipeline.
/// </summary>
public static partial class SpecBriefParityChecker
{
    private const int MaxScreens = 30;
    private const int MaxRules = 40;

    /// <summary>
    /// Trả về báo cáo lệch (để nối vào prompt vòng sửa) hoặc <c>null</c> khi không phát hiện lệch /
    /// không đủ dữ liệu để so (Brief thiếu mục tương ứng, spec không parse được).
    /// </summary>
    public static string? Check(string? productBrief, string? aiDesignSpec)
    {
        var spec = PocSpec.Parse(aiDesignSpec);

        // Spec không parse được MỤC NÀO (bản viết trước khi prompt pin định dạng, hoặc lượt sinh hỏng):
        // không có gì để so, và nếu cứ so thì mọi tầng đều báo "rơi rụng hết" — một báo cáo vô dụng đẩy
        // BA vào vòng sửa mù. Fail-open như trước.
        if (spec.Screens.Count == 0 && spec.Rules.Count == 0
            && spec.AcceptanceCriteria.Count == 0 && spec.WorkedExamples.Count == 0)
            return null;

        var sb = new StringBuilder();
        AppendScreenGap(sb, productBrief, spec);
        AppendRuleGap(sb, productBrief, spec);
        AppendAcceptanceGap(sb, productBrief, spec);

        return sb.Length == 0 ? null : sb.ToString().TrimEnd();
    }

    // Tầng 1 — màn hình. So khớp fuzzy dùng chung PocSpec.Matches với audit POC, để "màn hình đã có" ở
    // đây và "màn hình đã phủ" lúc audit không bao giờ hiểu khác nhau.
    private static void AppendScreenGap(StringBuilder sb, string? productBrief, PocSpec spec)
    {
        var briefScreens = ParseBriefScreens(productBrief);
        if (briefScreens.Count == 0 || spec.Screens.Count == 0)
            return;

        var missing = briefScreens
            .Where(b => !spec.Screens.Any(s => PocSpec.Matches(b, s)))
            .ToList();
        if (missing.Count == 0)
            return;

        sb.AppendLine("MÀN HÌNH RƠI RỤNG — các màn hình sau có trong mục \"Các màn hình chính\" của Product Brief nhưng KHÔNG có heading tương ứng trong \"## 6. Screens To Generate\" của AI Design Spec:");
        foreach (var screen in missing)
            sb.AppendLine($"- {screen}");
        sb.AppendLine("Bổ sung cho MỖI màn hình trên một heading `### 6.n. <Tên màn hình>` kèm đầy đủ chi tiết (route, mục đích, thành phần, field, nút, validation).");
        sb.AppendLine();
    }

    // Tầng 2 — quy tắc nghiệp vụ. Rule là CÂU chứ không phải nhãn ngắn nên không so bằng nhau được:
    // spec diễn đạt lại cùng một quy tắc bằng từ ngữ kỹ thuật hơn là chuyện bình thường và không phải
    // lỗi. Dùng TextSimilarity (đủ tỷ lệ từ chung là coi như đã có) để chỉ báo thứ THẬT SỰ biến mất.
    private static void AppendRuleGap(StringBuilder sb, string? productBrief, PocSpec spec)
    {
        var briefRules = ParseBriefRules(productBrief);
        if (briefRules.Count == 0 || spec.Rules.Count == 0)
            return;

        var missing = briefRules
            .Where(b => !spec.Rules.Any(s => TextSimilarity.Matches(b, s)))
            .ToList();
        if (missing.Count == 0)
            return;

        sb.AppendLine("QUY TẮC NGHIỆP VỤ RƠI RỤNG — các quy tắc sau có trong mục \"Quy tắc cần nhớ\" của Product Brief nhưng KHÔNG có bullet tương ứng trong \"## 10. Business Rules\" của AI Design Spec. Đây là thứ POC phải demo được thành hành vi thật (tính toán, chặn khi không hợp lệ, đổi trạng thái), nên thiếu ở spec là bản demo chạy sai nghiệp vụ mà không cổng nào phía sau thấy:");
        foreach (var rule in missing)
            sb.AppendLine($"- {rule}");
        sb.AppendLine("Bổ sung cho MỖI quy tắc trên một bullet `- BR-n: <phát biểu rule>` ở mục \"## 10. Business Rules\" (một dòng, demo được).");
        sb.AppendLine();
    }

    // Tầng 3 — câu nghiệm thu. Khác hai tầng trên, đây là chữ NGUYÊN VĂN của người dùng nên prompt bắt
    // chép lại y hệt; vẫn dùng so khớp nới để một dấu câu khác đi không thành báo cáo lệch giả.
    private static void AppendAcceptanceGap(StringBuilder sb, string? productBrief, PocSpec spec)
    {
        var briefCriteria = BriefAcceptanceCriteria.Parse(productBrief);
        if (briefCriteria.Count == 0)
            return;

        var missing = briefCriteria
            .Where(b => !spec.AcceptanceCriteria.Any(s => TextSimilarity.Matches(b.Criterion, s.Text)))
            .ToList();
        if (missing.Count == 0)
            return;

        sb.AppendLine("CÂU NGHIỆM THU RƠI RỤNG — các dòng \"Hoàn thành khi: …\" sau có trong Product Brief người dùng ĐÃ DUYỆT nhưng KHÔNG có bullet tương ứng trong \"## 14. Acceptance Criteria\" của AI Design Spec. Đây là chữ của chính người dùng và là đích mà bộ kịch bản nghiệm thu (UAT) được sinh theo — mất ở đây nghĩa là một điều đã hứa với họ sẽ không được kiểm ở bất kỳ đâu:");
        sb.Append(BriefAcceptanceCriteria.Render(missing));
        sb.AppendLine("Bổ sung mục \"## 14. Acceptance Criteria\" với ĐỦ mọi câu nghiệm thu của Product Brief, chép NGUYÊN VĂN theo định dạng `- AC-n (<tên tính năng>): <câu nghiệm thu>` và đánh số lại liên tục từ AC-1.");
        sb.AppendLine();
    }

    /// <summary>
    /// Tên các màn hình trong mục "## Các màn hình chính" của Product Brief: mỗi bullet một màn hình,
    /// tên là phần trước dấu ':' / '—' / '–' (phần sau là mô tả).
    /// </summary>
    public static IReadOnlyList<string> ParseBriefScreens(string? productBrief) =>
        ParseBriefSection(
            productBrief,
            heading => heading.Contains("màn hình", StringComparison.OrdinalIgnoreCase)
                       || heading.Contains("screen", StringComparison.OrdinalIgnoreCase),
            TakeNameBeforeSeparator,
            MaxScreens);

    /// <summary>
    /// Các quy tắc trong mục "## Quy tắc cần nhớ" của Product Brief — giữ NGUYÊN cả câu (khác màn hình:
    /// một quy tắc là phát biểu, cắt ở dấu ':' sẽ mất đúng phần mang nội dung).
    /// </summary>
    public static IReadOnlyList<string> ParseBriefRules(string? productBrief) =>
        ParseBriefSection(
            productBrief,
            heading => heading.Contains("quy tắc", StringComparison.OrdinalIgnoreCase)
                       || heading.Contains("rule", StringComparison.OrdinalIgnoreCase),
            text => text,
            MaxRules);

    // Bullet cấp 0 của MỘT mục "## …" chọn theo vị từ tiêu đề, đã bỏ nhấn mạnh markdown và trùng lặp.
    private static IReadOnlyList<string> ParseBriefSection(
        string? productBrief,
        Func<string, bool> isWantedHeading,
        Func<string, string> transform,
        int cap)
    {
        if (string.IsNullOrWhiteSpace(productBrief))
            return Array.Empty<string>();

        var items = new List<string>();
        var inSection = false;

        foreach (var raw in productBrief.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();

            var heading = Level2HeadingRegex().Match(line);
            if (heading.Success)
            {
                inSection = isWantedHeading(heading.Groups[1].Value);
                continue;
            }

            if (!inSection)
                continue;

            var bullet = BulletRegex().Match(line);
            if (!bullet.Success)
                continue;

            var value = transform(bullet.Groups[1].Value.Replace("**", string.Empty).Trim()).Trim();
            if (value.Length > 0 && !items.Any(x => PocSpec.Key(x) == PocSpec.Key(value)))
                items.Add(value);

            if (items.Count >= cap)
                break;
        }

        return items;
    }

    private static string TakeNameBeforeSeparator(string text)
    {
        var sep = text.IndexOfAny([':', '—', '–']);
        return sep > 0 ? text[..sep].Trim() : text;
    }

    [GeneratedRegex(@"^##(?!#)\s*(.+)$")]
    private static partial Regex Level2HeadingRegex();

    [GeneratedRegex(@"^[-*+]\s+(.+)$")]
    private static partial Regex BulletRegex();
}
