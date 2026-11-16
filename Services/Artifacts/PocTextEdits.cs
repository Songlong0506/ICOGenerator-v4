using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Artifacts;

/// <summary>Một chỗ sửa "tìm đúng đoạn này, thay bằng đoạn kia" trong poc-demo.html.</summary>
public sealed record PocTextEdit(string Find, string Replace);

/// <summary>Áp các <see cref="PocTextEdit"/> lên vùng nội dung/vùng script của bản demo — dùng bởi tool <c>FixPocAndAudit</c>.</summary>
public static class PocTextEdits
{
    /// <summary>
    /// Áp lần lượt các chỗ sửa lên các VÙNG của bản demo (<paramref name="regions"/> — vùng nội dung và vùng
    /// script). Một chỗ sửa chỉ được áp khi đoạn cần tìm khớp ĐÚNG MỘT chỗ trên tất cả các vùng — cùng luật
    /// với tool <c>ReplaceInFile</c>, vì thay nhầm chỗ thứ hai là hỏng một màn hình khác mà không ai biết.
    /// Không tìm trong phần shell: một đoạn khớp ở đó chỉ có thể là khớp nhầm. Khớp nguyên văn trước; không
    /// thấy thì thử bản đã bỏ khoảng trắng đầu/cuối (model hay lệch một dòng trống quanh khối). Chỗ không áp
    /// được không làm hỏng các chỗ khác: nó được trả về để vòng sửa sau biết.
    /// </summary>
    public static (string[] Regions, IReadOnlyList<string> Failures) Apply(IReadOnlyList<string> regions, IEnumerable<PocTextEdit> edits)
    {
        var result = regions.ToArray();
        var failures = new List<string>();
        foreach (var edit in edits)
        {
            var (find, replace) = (edit.Find, edit.Replace);
            var count = CountAll(result, find);
            if (count == 0)
            {
                var trimmed = find.Trim();
                var trimmedCount = CountAll(result, trimmed);
                if (trimmedCount > 0)
                    (find, replace, count) = (trimmed, replace.Trim(), trimmedCount);
            }

            if (count == 1)
            {
                var region = Array.FindIndex(result, r => Count(r, find) == 1);
                var index = result[region].IndexOf(find, StringComparison.Ordinal);
                result[region] = result[region][..index] + replace + result[region][(index + find.Length)..];
                continue;
            }

            failures.Add(count == 0
                ? $"Không tìm thấy đoạn cần sửa: «{Preview(edit.Find)}»"
                : $"Đoạn cần sửa khớp {count} chỗ (phải khớp đúng một chỗ — thêm ngữ cảnh xung quanh): «{Preview(edit.Find)}»");
        }

        return (result, failures);
    }

    private static int CountAll(IEnumerable<string> regions, string needle) => regions.Sum(r => Count(r, needle));

    private static int Count(string haystack, string needle)
    {
        if (needle.Length == 0)
            return 0;
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string Preview(string text)
    {
        var oneLine = Regex.Replace(text.Trim(), @"\s+", " ");
        return oneLine.Length <= 160 ? oneLine : oneLine[..160] + "…";
    }
}
