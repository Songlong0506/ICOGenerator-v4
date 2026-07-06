namespace ICOGenerator.Services.Requirements;

public enum DiffLineKind
{
    Same,
    Added,
    Removed
}

public sealed record DiffLine(DiffLineKind Kind, string Text);

/// <summary>
/// Diff hai bản text theo DÒNG (LCS) cho màn hình lịch sử tài liệu — thuần in-memory, không phụ thuộc
/// package ngoài. Tài liệu sinh ra thường chỉ đổi cục bộ nên trước khi chạy LCS O(n·m) ta cắt phần đầu/
/// cuối giống hệt nhau (rẻ, O(n)); phần giữa còn lại nếu vẫn quá lớn (quá <see cref="MaxLcsCells"/> ô DP)
/// thì trả diff "thay cả khối" (toàn bộ cũ Removed + toàn bộ mới Added) thay vì để một tài liệu bất thường
/// đốt CPU/RAM của request.
/// </summary>
public class DocumentDiffService
{
    // 4M ô DP int ≈ 16MB — trần an toàn cho một request; tài liệu docx bóc text hiếm khi chạm.
    private const int MaxLcsCells = 4_000_000;

    public IReadOnlyList<DiffLine> Diff(string? oldText, string? newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        // Cắt phần đầu + cuối chung để thu nhỏ bài toán LCS về đúng vùng thay đổi.
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length
               && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
            prefix++;

        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix
               && string.Equals(oldLines[^(suffix + 1)], newLines[^(suffix + 1)], StringComparison.Ordinal))
            suffix++;

        var oldMiddle = oldLines.AsSpan(prefix, oldLines.Length - prefix - suffix);
        var newMiddle = newLines.AsSpan(prefix, newLines.Length - prefix - suffix);

        var result = new List<DiffLine>(oldLines.Length + newLines.Length);

        for (var i = 0; i < prefix; i++)
            result.Add(new DiffLine(DiffLineKind.Same, oldLines[i]));

        AppendMiddleDiff(result, oldMiddle, newMiddle);

        for (var i = suffix; i >= 1; i--)
            result.Add(new DiffLine(DiffLineKind.Same, oldLines[^i]));

        return result;
    }

    private static void AppendMiddleDiff(List<DiffLine> result, ReadOnlySpan<string> oldLines, ReadOnlySpan<string> newLines)
    {
        if (oldLines.Length == 0 && newLines.Length == 0)
            return;

        // Vùng đổi quá lớn cho DP ⇒ diff thô "thay cả khối" (vẫn đúng ngữ nghĩa, chỉ kém mịn).
        if ((long)oldLines.Length * newLines.Length > MaxLcsCells)
        {
            foreach (var line in oldLines)
                result.Add(new DiffLine(DiffLineKind.Removed, line));
            foreach (var line in newLines)
                result.Add(new DiffLine(DiffLineKind.Added, line));
            return;
        }

        // LCS chuẩn: bảng (n+1)×(m+1) độ dài chuỗi chung dài nhất, rồi lần ngược để phát dòng.
        var n = oldLines.Length;
        var m = newLines.Length;
        var dp = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
            {
                result.Add(new DiffLine(DiffLineKind.Same, oldLines[x]));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                result.Add(new DiffLine(DiffLineKind.Removed, oldLines[x]));
                x++;
            }
            else
            {
                result.Add(new DiffLine(DiffLineKind.Added, newLines[y]));
                y++;
            }
        }
        for (; x < n; x++)
            result.Add(new DiffLine(DiffLineKind.Removed, oldLines[x]));
        for (; y < m; y++)
            result.Add(new DiffLine(DiffLineKind.Added, newLines[y]));
    }

    private static string[] SplitLines(string? text) =>
        string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : text.Replace("\r\n", "\n").Split('\n');
}
