using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TeamExport.Stripping;

/// <summary>
/// Xoá comment C# bằng Roslyn (không dùng regex: regex sẽ cắt nhầm "//" nằm trong chuỗi, verbatim string,
/// raw string...). Sau khi xoá, parse lại và so chuỗi token với bản gốc — lệch một token là ném lỗi, nên
/// không có đường nào để một file hỏng lọt sang repo team.
/// </summary>
public static class CSharpCommentStripper
{
    private static readonly CSharpParseOptions ParseOptions = new(
        LanguageVersion.Preview, DocumentationMode.Parse, SourceCodeKind.Regular);

    public static string Strip(string text, IReadOnlyCollection<string> keepMarkers)
    {
        var original = CSharpSyntaxTree.ParseText(text, ParseOptions).GetRoot();
        var spans = original.DescendantTrivia(descendIntoTrivia: true)
            .Where(IsComment)
            .Where(t => !ShouldKeep(t.ToFullString(), keepMarkers))
            .Select(t => new CommentSpan(t.FullSpan.Start, t.FullSpan.End))
            .ToList();
        if (spans.Count == 0)
            return text;

        var result = CommentSpanRemover.Remove(text, spans);
        Verify(original, result, keepMarkers);
        return result;
    }

    private static bool IsComment(SyntaxTrivia t) => t.Kind() is
        SyntaxKind.SingleLineCommentTrivia or
        SyntaxKind.MultiLineCommentTrivia or
        SyntaxKind.SingleLineDocumentationCommentTrivia or
        SyntaxKind.MultiLineDocumentationCommentTrivia;

    internal static bool ShouldKeep(string comment, IReadOnlyCollection<string> keepMarkers) =>
        keepMarkers.Any(m => comment.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static void Verify(SyntaxNode original, string result, IReadOnlyCollection<string> keepMarkers)
    {
        var stripped = CSharpSyntaxTree.ParseText(result, ParseOptions).GetRoot();

        var before = original.DescendantTokens(descendIntoTrivia: true).Select(TokenKey).ToList();
        var after = stripped.DescendantTokens(descendIntoTrivia: true).Select(TokenKey).ToList();
        // Token nằm trong doc comment (XmlText...) biến mất cùng comment — chỉ so token thật của code.
        before = before.Where(k => k.Length > 0).ToList();
        after = after.Where(k => k.Length > 0).ToList();
        if (!before.SequenceEqual(after))
        {
            var idx = before.Zip(after).TakeWhile(p => p.First == p.Second).Count();
            throw new InvalidOperationException(
                $"Sau khi xoá comment, chuỗi token C# bị lệch tại token #{idx} " +
                $"('{before.ElementAtOrDefault(idx)}' → '{after.ElementAtOrDefault(idx)}').");
        }

        var leftover = stripped.DescendantTrivia(descendIntoTrivia: true)
            .Where(IsComment)
            .FirstOrDefault(t => !ShouldKeep(t.ToFullString(), keepMarkers));
        if (leftover != default)
            throw new InvalidOperationException($"Còn sót comment sau khi xoá: {leftover.ToString().Trim()}");
    }

    private static string TokenKey(SyntaxToken token)
    {
        if (token.Parent is not null && token.Parent.AncestorsAndSelf().Any(n =>
                n is Microsoft.CodeAnalysis.CSharp.Syntax.DocumentationCommentTriviaSyntax))
            return "";
        return token.Kind() + ":" + token.Text;
    }
}
