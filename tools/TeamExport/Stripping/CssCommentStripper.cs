namespace TeamExport.Stripping;

/// <summary>Xoá comment <c>/* */</c> trong CSS (bỏ qua nội dung chuỗi; giữ comment <c>/*! ... */</c>).</summary>
public static class CssCommentStripper
{
    public static string Strip(string text, IReadOnlyCollection<string> keepMarkers) =>
        CommentSpanRemover.Remove(text, FindComments(text, 0, text.Length, keepMarkers));

    public static List<CommentSpan> FindComments(string text, int from, int to, IReadOnlyCollection<string> keepMarkers)
    {
        var spans = new List<CommentSpan>();
        var i = from;
        while (i < to)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                i++;
                while (i < to && text[i] != c && text[i] != '\n')
                    i += text[i] == '\\' ? 2 : 1;
                i++;
            }
            else if (c == '/' && i + 1 < to && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, to - i - 2, StringComparison.Ordinal);
                end = end < 0 ? to : end + 2;
                var body = text[i..end];
                if (!body.StartsWith("/*!", StringComparison.Ordinal) && !CSharpCommentStripper.ShouldKeep(body, keepMarkers))
                    spans.Add(new CommentSpan(i, end));
                i = end;
            }
            else
            {
                i++;
            }
        }
        return spans;
    }
}
