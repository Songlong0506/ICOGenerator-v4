namespace TeamExport.Stripping;

/// <summary>Xoá comment <c>&lt;!-- --&gt;</c> trong file XML (.csproj/.props/.targets), bỏ qua CDATA.</summary>
public static class XmlCommentStripper
{
    public static string Strip(string text, IReadOnlyCollection<string> keepMarkers)
    {
        var spans = new List<CommentSpan>();
        var i = 0;
        while (i < text.Length)
        {
            if (string.CompareOrdinal(text, i, "<![CDATA[", 0, 9) == 0)
            {
                var end = text.IndexOf("]]>", i, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 3;
            }
            else if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                var end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 3;
                if (!CSharpCommentStripper.ShouldKeep(text[i..end], keepMarkers))
                    spans.Add(new CommentSpan(i, end));
                i = end;
            }
            else
            {
                i++;
            }
        }
        return CommentSpanRemover.Remove(text, spans);
    }
}
