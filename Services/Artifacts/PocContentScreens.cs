using System.Net;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Rút nhãn các màn hình (data-view của <c>&lt;section class="page-view"&gt;</c>) từ MỘT đoạn content mà
/// Developer agent nạp qua SetPocContent/AppendPocContent — để tường thuật tiến độ "đã dựng màn hình X"
/// cho người dùng trong lúc chờ POC (thay vì feed token câm). Chỉ đếm SECTION page-view: modal/CRUD form
/// và markup khác không phải một màn hình nên bị bỏ qua. Quét chuỗi thuần như <see cref="PocAudit"/> —
/// markup do shell + Bootstrap định hình nên không cần parser HTML.
/// </summary>
public static partial class PocContentScreens
{
    public static IReadOnlyList<string> Extract(string? content)
    {
        var labels = new List<string>();
        if (string.IsNullOrEmpty(content))
            return labels;

        foreach (Match tag in SectionTagRegex().Matches(content))
        {
            if (!tag.Value.Contains("page-view", StringComparison.Ordinal))
                continue;
            var view = DataViewRegex().Match(tag.Value);
            if (!view.Success)
                continue;
            var text = WebUtility.HtmlDecode(view.Groups[1].Value).Trim();
            if (text.Length > 0)
                labels.Add(text);
        }
        return labels;
    }

    [GeneratedRegex("<section\\b[^>]*>")]
    private static partial Regex SectionTagRegex();

    [GeneratedRegex("data-view=\"([^\"]*)\"")]
    private static partial Regex DataViewRegex();
}
