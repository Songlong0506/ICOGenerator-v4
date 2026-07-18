using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Bóc mục "## 12. Assumptions" (các giả định BA tự đưa khi sinh AI Design Spec) ra khỏi spec markdown
/// để hiển thị cho người dùng thường. Lý do tồn tại: spec được phép "tự đưa giả định hợp lý" rồi đi
/// thẳng vào bước dựng POC không qua mắt người dùng — panel giả định là chỗ duy nhất user thấy các
/// quyết định thay mặt mình TRƯỚC khi POC hiện ra "lạ lạ". Chịu lỗi: không có mục → danh sách rỗng.
/// </summary>
public static partial class SpecAssumptionsParser
{
    private const int MaxItems = 30;

    public static IReadOnlyList<string> Parse(string? specMarkdown)
    {
        if (string.IsNullOrWhiteSpace(specMarkdown))
            return Array.Empty<string>();

        var items = new List<string>();
        var inSection = false;
        foreach (var raw in specMarkdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();

            var heading = Level2HeadingRegex().Match(line);
            if (heading.Success)
            {
                var text = heading.Groups[1].Value;
                inSection = text.Contains("assumption", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("giả định", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
                continue;

            var bullet = BulletRegex().Match(line);
            if (!bullet.Success)
                continue;

            var value = bullet.Groups[1].Value.Replace("**", string.Empty).Trim();
            // Placeholder "Không có" nghĩa là spec không có giả định nào — đừng hiển thị nó như một giả định.
            if (value.Length == 0 || PlaceholderRegex().IsMatch(value))
                continue;

            items.Add(value);
            if (items.Count >= MaxItems)
                break;
        }

        return items;
    }

    [GeneratedRegex(@"^##(?!#)\s*(.+)$")]
    private static partial Regex Level2HeadingRegex();

    [GeneratedRegex(@"^[-*+]\s+(.+)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^(?:không có|n/?a|none)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderRegex();
}
