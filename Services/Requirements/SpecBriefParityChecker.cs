using System.Text;
using System.Text.RegularExpressions;
using ICOGenerator.Services.Artifacts;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Đối chiếu deterministic giữa Product Brief đã duyệt và AI Design Spec vừa sinh: mọi màn hình ở mục
/// "## Các màn hình chính" của Brief phải có mặt trong "§ Screens To Generate" của spec (so khớp fuzzy
/// dùng chung <see cref="PocSpec.Matches"/> với audit POC). Lý do tồn tại: prompt yêu cầu spec "cùng một
/// sản phẩm" với Brief nhưng không gì kiểm chứng — một màn hình rơi rụng ở bước này là POC thiếu luôn
/// tính năng mà mọi cổng sau đều không thấy (audit POC chỉ so với spec). Phát hiện lệch thì caller cho
/// BA sửa lại MỘT vòng (xem RequirementDocsService); fail-open — Brief không có mục màn hình thì bỏ qua.
/// </summary>
public static partial class SpecBriefParityChecker
{
    private const int MaxScreens = 30;

    /// <summary>
    /// Trả về báo cáo lệch (để nối vào prompt vòng sửa) hoặc <c>null</c> khi không phát hiện lệch /
    /// không đủ dữ liệu để so (Brief không có mục màn hình, spec không parse được).
    /// </summary>
    public static string? Check(string? productBrief, string? aiDesignSpec)
    {
        var briefScreens = ParseBriefScreens(productBrief);
        if (briefScreens.Count == 0)
            return null;

        var spec = PocSpec.Parse(aiDesignSpec);
        if (spec.Screens.Count == 0)
            return null;

        var missing = briefScreens
            .Where(b => !spec.Screens.Any(s => PocSpec.Matches(b, s)))
            .ToList();

        if (missing.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("Đối chiếu tự động với Product Brief phát hiện màn hình bị RƠI RỤNG — các màn hình sau có trong mục \"Các màn hình chính\" của Product Brief nhưng KHÔNG có heading tương ứng trong \"## 6. Screens To Generate\" của AI Design Spec:");
        foreach (var screen in missing)
            sb.AppendLine($"- {screen}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Tên các màn hình trong mục "## Các màn hình chính" của Product Brief: mỗi bullet một màn hình,
    /// tên là phần trước dấu ':' / '—' / '–' (phần sau là mô tả).
    /// </summary>
    public static IReadOnlyList<string> ParseBriefScreens(string? productBrief)
    {
        if (string.IsNullOrWhiteSpace(productBrief))
            return Array.Empty<string>();

        var screens = new List<string>();
        var inSection = false;
        foreach (var raw in productBrief.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();

            var heading = Level2HeadingRegex().Match(line);
            if (heading.Success)
            {
                inSection = heading.Groups[1].Value.Contains("màn hình", StringComparison.OrdinalIgnoreCase)
                            || heading.Groups[1].Value.Contains("screen", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
                continue;

            var bullet = BulletRegex().Match(line);
            if (!bullet.Success)
                continue;

            var name = bullet.Groups[1].Value.Replace("**", string.Empty).Trim();
            // Tên màn hình đứng trước dấu phân cách mô tả (nếu có).
            var sep = name.IndexOfAny([':', '—', '–']);
            if (sep > 0)
                name = name[..sep].Trim();

            if (name.Length > 0 && !screens.Any(x => PocSpec.Key(x) == PocSpec.Key(name)))
                screens.Add(name);

            if (screens.Count >= MaxScreens)
                break;
        }

        return screens;
    }

    [GeneratedRegex(@"^##(?!#)\s*(.+)$")]
    private static partial Regex Level2HeadingRegex();

    [GeneratedRegex(@"^[-*+]\s+(.+)$")]
    private static partial Regex BulletRegex();
}
