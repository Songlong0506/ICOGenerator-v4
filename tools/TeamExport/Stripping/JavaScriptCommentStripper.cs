using Acornima;

namespace TeamExport.Stripping;

/// <summary>
/// Tìm comment JavaScript bằng parser thật (Acornima) — phân biệt được regex <c>/\/\//</c>, chuỗi,
/// template literal với comment, việc mà regex không làm nổi. Sau khi xoá, tokenize lại và so với bản gốc.
/// </summary>
public static class JavaScriptCommentStripper
{
    /// <summary>Xoá comment trong một file .js độc lập. Ném lỗi nếu file không parse được.</summary>
    public static string Strip(string text, IReadOnlyCollection<string> keepMarkers)
    {
        var spans = FindComments(text, keepMarkers);
        if (spans.Count == 0)
            return text;
        var result = CommentSpanRemover.Remove(text, spans);
        Verify(text, result);
        return result;
    }

    /// <summary>
    /// Vị trí các comment cần xoá. Ném <see cref="ParseErrorException"/> nếu đoạn mã không phải JavaScript hợp lệ.
    /// </summary>
    public static List<CommentSpan> FindComments(string text, IReadOnlyCollection<string> keepMarkers)
    {
        var spans = new List<CommentSpan>();
        var options = new ParserOptions
        {
            AllowReturnOutsideFunction = true,
            AllowAwaitOutsideFunction = true,
            OnComment = (in Comment c) =>
            {
                if (c.Kind is not (CommentKind.Line or CommentKind.Block))
                    return;
                var body = text[c.Range.Start..c.Range.End];
                if (body.StartsWith("/*!", StringComparison.Ordinal) ||
                    CSharpCommentStripper.ShouldKeep(body, keepMarkers))
                    return;
                spans.Add(new CommentSpan(c.Range.Start, c.Range.End));
            }
        };
        Parse(text, options, spans.Clear);
        return spans;
    }

    private static void Parse(string text, ParserOptions options, Action reset)
    {
        try
        {
            new Parser(options).ParseScript(text);
        }
        catch (ParseErrorException)
        {
            // Có thể là ES module (import/export): parse lại từ đầu, bỏ kết quả của lượt parse dở.
            reset();
            new Parser(options).ParseModule(text);
        }
    }

    private static void Verify(string original, string result)
    {
        var before = Tokens(original);
        var after = Tokens(result);
        if (!before.SequenceEqual(after))
        {
            var idx = before.Zip(after).TakeWhile(p => p.First == p.Second).Count();
            throw new InvalidOperationException(
                $"Sau khi xoá comment, chuỗi token JavaScript bị lệch tại token #{idx} " +
                $"('{before.ElementAtOrDefault(idx)}' → '{after.ElementAtOrDefault(idx)}').");
        }
    }

    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var options = new ParserOptions
        {
            AllowReturnOutsideFunction = true,
            AllowAwaitOutsideFunction = true,
            OnToken = (in Token t) => tokens.Add(t.Kind + ":" + text[t.Range.Start..t.Range.End])
        };
        Parse(text, options, tokens.Clear);
        return tokens;
    }
}
