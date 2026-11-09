namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Chỉ ra lỗi JS mà trình duyệt bắt được (sự kiện <c>error</c>: thông điệp + dòng/cột TRONG TÀI LIỆU) nằm ở
/// dòng nào của vùng <c>POC_SCRIPT</c>, kèm chính dòng code đó.
///
/// <para>
/// Vì sao cần: Playwright chỉ đưa thông điệp của lỗi (<c>SyntaxError: Unexpected token '}'</c>), không có vị
/// trí — với lỗi cú pháp thì stack cũng rỗng. Model nhận "script có lỗi cú pháp" mà không biết ở đâu nên đi
/// săn: ca thật với <c>claude-sonnet-5-5</c> tốn 5 lượt agent (tìm <c>";;"</c> bằng SearchInFiles, đọc lại cả
/// poc-demo.html bằng ReadFile làm prompt nhảy +53k token, tự viết <c>chk.js</c> rồi chạy <c>node</c>) — mỗi
/// lượt gửi lại toàn bộ lịch sử. Có số dòng + dòng code thì sửa bằng đúng một <c>FixPocAndAudit</c>.
/// </para>
/// </summary>
public static class PocScriptErrorLocator
{
    private const int MaxSnippetChars = 160;

    /// <summary>
    /// Mô tả vị trí của lỗi, hoặc <c>null</c> khi dòng đó không nằm trong vùng script của POC (lỗi trong
    /// script shell, hoặc trình duyệt không báo dòng) — khi đó dòng "Lỗi JS" chung vẫn còn trong báo cáo.
    /// </summary>
    /// <param name="html">Nội dung poc-demo.html đúng như trình duyệt đã mở.</param>
    /// <param name="documentLine">Số dòng 1-based trong tài liệu (<c>ErrorEvent.lineno</c>).</param>
    /// <param name="column">Số cột 1-based (<c>ErrorEvent.colno</c>), 0 nếu không có.</param>
    public static string? Describe(string html, int documentLine, int column, string message)
    {
        if (documentLine <= 0 || string.IsNullOrEmpty(html))
            return null;

        var bodyFirstLine = ScriptBodyFirstLine(html);
        if (bodyFirstLine is not int first)
            return null;

        var lines = html.Split('\n');
        var scriptLines = PocTemplate.GetScriptBody(html).Split('\n').Length;
        var relative = documentLine - first + 1;
        if (relative < 1 || relative > scriptLines || documentLine > lines.Length)
            return null;

        var code = lines[documentLine - 1].TrimEnd('\r');
        var snippet = Snippet(code, column);
        var previous = documentLine >= 2 && relative >= 2 ? Snippet(lines[documentLine - 2].TrimEnd('\r'), 0) : null;
        var where = column > 0 ? $"dòng {relative}, cột {column}" : $"dòng {relative}";
        return $"Vị trí lỗi JS «{message.Trim()}»: {where} của vùng script (POC_SCRIPT) — dòng đó: `{snippet}`"
            + (string.IsNullOrWhiteSpace(previous) ? string.Empty : $"; dòng trước: `{previous}`")
            + ". Sửa thẳng đoạn này bằng FixPocAndAudit, không cần đọc lại file.";
    }

    // Dòng (1-based) đầu tiên của thân <script> trong vùng POC_SCRIPT — thân bắt đầu ngay sau dòng "<script>".
    private static int? ScriptBodyFirstLine(string html)
    {
        var marker = html.IndexOf(PocTemplate.ScriptStartMarker, StringComparison.Ordinal);
        if (marker < 0)
            return null;
        var open = html.IndexOf("<script", marker, StringComparison.OrdinalIgnoreCase);
        if (open < 0)
            return null;
        var openEnd = html.IndexOf('>', open);
        if (openEnd < 0)
            return null;

        var line = 1;
        for (var i = 0; i <= openEnd; i++)
        {
            if (html[i] == '\n')
                line++;
        }

        // Thân bắt đầu ở dòng KẾ dòng chứa "<script>" khi ngay sau '>' là xuống dòng, ngược lại cùng dòng.
        var next = openEnd + 1;
        if (next < html.Length && html[next] == '\r')
            next++;
        return next < html.Length && html[next] == '\n' ? line + 1 : line;
    }

    // Một dòng minified có thể dài cả nghìn ký tự: cắt quanh cột lỗi.
    private static string Snippet(string code, int column)
    {
        var trimmed = code.Trim();
        if (trimmed.Length <= MaxSnippetChars)
            return trimmed;

        var lead = code.Length - code.TrimStart().Length;
        var center = column > 0 ? Math.Clamp(column - 1 - lead, 0, trimmed.Length) : 0;
        var start = Math.Clamp(center - MaxSnippetChars / 2, 0, trimmed.Length - MaxSnippetChars);
        return (start > 0 ? "…" : string.Empty) + trimmed.Substring(start, MaxSnippetChars) + "…";
    }
}
