namespace TeamExport.Stripping;

/// <summary>
/// Xoá comment trong file Razor (.cshtml): <c>@* *@</c>, <c>&lt;!-- --&gt;</c>, comment C# <c>//</c> và
/// <c>/* */</c> trong khối code (<c>@{ }</c>, <c>@functions</c>, thân <c>@if/@foreach...</c>), comment JS trong
/// <c>&lt;script&gt;</c> và comment CSS trong <c>&lt;style&gt;</c>.
///
/// Đây là một bộ quét Razor tối giản chứ không phải compiler Razor: nó chỉ cần biết ĐANG ở ngữ cảnh nào
/// (markup / C# / script / style) để không coi "//" trong một URL của markup là comment. Chốt chặn sau
/// cùng là bước <c>dotnet build</c> của bản xuất (Razor được biên dịch lúc build), cộng với kiểm tra mỗi
/// đoạn bị xoá phải mở đầu bằng đúng cú pháp comment.
/// </summary>
public sealed class RazorCommentStripper
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"
    };

    private static readonly HashSet<string> Directives = new(StringComparer.Ordinal)
    {
        "model", "inject", "addTagHelper", "removeTagHelper", "tagHelperPrefix", "layout", "inherits",
        "implements", "namespace", "page", "attribute", "preservewhitespace", "typeparam", "rendermode"
    };

    private static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "using", "lock", "try", "do"
    };

    private readonly string _t;
    private readonly IReadOnlyCollection<string> _keep;
    private readonly List<CommentSpan> _spans = new();
    private readonly List<string> _warnings = new();
    private int _i;

    private RazorCommentStripper(string text, IReadOnlyCollection<string> keepMarkers)
    {
        _t = text;
        _keep = keepMarkers;
    }

    public static string Strip(string text, IReadOnlyCollection<string> keepMarkers, Action<string>? warn = null)
    {
        var scanner = new RazorCommentStripper(text, keepMarkers);
        scanner.ScanMarkup(MarkupEnd.Eof, null);
        foreach (var w in scanner._warnings)
            warn?.Invoke(w);

        foreach (var s in scanner._spans)
        {
            var body = text[s.Start..s.End];
            if (!(body.StartsWith("@*") || body.StartsWith("<!--") || body.StartsWith("//") || body.StartsWith("/*")))
                throw new InvalidOperationException($"Đoạn bị xoá không phải comment: '{Truncate(body)}'");
        }
        return CommentSpanRemover.Remove(text, scanner._spans);
    }

    private enum MarkupEnd { Eof, CloseTag, Brace }

    // ---------------------------------------------------------------- markup

    private void ScanMarkup(MarkupEnd end, string? closeTag)
    {
        while (_i < _t.Length)
        {
            var c = _t[_i];
            if (c == '@')
            {
                ScanAt(inCode: false);
            }
            else if (c == '<' && At("<!--"))
            {
                AddComment(_i, IndexAfter("-->", _i + 4));
            }
            else if (c == '<' && At("</"))
            {
                var nameStart = _i + 2;
                var name = ReadName(nameStart);
                var gt = _t.IndexOf('>', nameStart);
                _i = gt < 0 ? _t.Length : gt + 1;
                if (end == MarkupEnd.CloseTag && string.Equals(name, closeTag, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            else if (c == '<' && _i + 1 < _t.Length && (char.IsLetter(_t[_i + 1]) || _t[_i + 1] == '!'))
            {
                ScanElement();
            }
            else if (c == '}' && end == MarkupEnd.Brace)
            {
                _i++;
                return;
            }
            else
            {
                _i++;
            }
        }
    }

    /// <summary>Đang đứng ở '&lt;' của thẻ mở: quét trọn phần tử (thẻ mở, nội dung, thẻ đóng).</summary>
    private void ScanElement()
    {
        if (At("<!"))
        {
            // <!DOCTYPE ...>
            var gt = _t.IndexOf('>', _i);
            _i = gt < 0 ? _t.Length : gt + 1;
            return;
        }

        var name = ReadName(_i + 1);
        var tagStart = _i;
        _i += 1 + name.Length;
        var selfClosing = ScanTagBody();
        if (selfClosing || VoidElements.Contains(name))
            return;

        if (name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            var contentStart = _i;
            var close = _t.IndexOf("</" + name, _i, StringComparison.OrdinalIgnoreCase);
            var contentEnd = close < 0 ? _t.Length : close;
            var openTag = _t[tagStart..contentStart];
            if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
                _spans.AddRange(CssCommentStripper.FindComments(_t, contentStart, contentEnd, _keep));
            else if (IsJavaScriptType(openTag))
                ScanScript(contentStart, contentEnd);
            var gt = close < 0 ? _t.Length - 1 : _t.IndexOf('>', close);
            _i = gt < 0 ? _t.Length : gt + 1;
            return;
        }

        ScanMarkup(MarkupEnd.CloseTag, name);
    }

    /// <summary>Quét phần thuộc tính của thẻ mở tới '&gt;'. Trả về true nếu là thẻ tự đóng "/&gt;".</summary>
    private bool ScanTagBody()
    {
        while (_i < _t.Length)
        {
            var c = _t[_i];
            if (c == '>')
            {
                var selfClosing = _i > 0 && _t[_i - 1] == '/';
                _i++;
                return selfClosing;
            }
            if (c is '"' or '\'')
            {
                _i++;
                while (_i < _t.Length && _t[_i] != c)
                {
                    if (_t[_i] == '@')
                        ScanAt(inCode: false, inAttribute: true);
                    else
                        _i++;
                }
                _i++;
            }
            else if (c == '@')
            {
                ScanAt(inCode: false, inAttribute: true);
            }
            else
            {
                _i++;
            }
        }
        return false;
    }

    private static bool IsJavaScriptType(string openTag)
    {
        var idx = openTag.IndexOf("type=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return true;
        var rest = openTag[(idx + 5)..].Trim('"', '\'', ' ').ToLowerInvariant();
        return rest.StartsWith("text/javascript") || rest.StartsWith("module") || rest.StartsWith("application/javascript");
    }

    /// <summary>
    /// Nội dung &lt;script&gt;: thay các biểu thức Razor (<c>@Model.Id</c>, <c>@(...)</c>, <c>@Html.Raw(...)</c>)
    /// bằng định danh cùng độ dài để parser JS đọc được, rồi lấy vị trí comment (giữ nguyên chỉ số).
    /// </summary>
    private void ScanScript(int from, int to)
    {
        var masked = _t[from..to].ToCharArray();
        var save = _i;
        _i = from;
        var razorComments = new List<CommentSpan>();
        while (_i < to)
        {
            if (_t[_i] == '@' && _i + 1 < to && _t[_i + 1] == '*')
            {
                var end = IndexAfter("*@", _i + 2);
                razorComments.Add(new CommentSpan(_i, end));
                Mask(masked, from, _i, end, ' ');
                _i = end;
            }
            else if (_t[_i] == '@' && _i + 1 < to && _t[_i + 1] == '@')
            {
                Mask(masked, from, _i, _i + 1, ' ');
                _i += 2;
            }
            else if (_t[_i] == '@' && _i + 1 < to && (_t[_i + 1] == '(' || IsIdentStart(_t[_i + 1])))
            {
                var start = _i;
                var spansBefore = _spans.Count;
                _i++;
                if (_t[_i] == '(')
                    SkipBalancedCSharp();
                else
                    SkipImplicitExpression();
                _spans.RemoveRange(spansBefore, _spans.Count - spansBefore);
                Mask(masked, from, start, _i, '_');
            }
            else
            {
                _i++;
            }
        }
        _i = save;
        _spans.AddRange(razorComments);

        try
        {
            foreach (var s in JavaScriptCommentStripper.FindComments(new string(masked), _keep))
                _spans.Add(new CommentSpan(from + s.Start, from + s.End));
        }
        catch (Acornima.ParseErrorException ex)
        {
            _warnings.Add($"Bỏ qua comment JS trong <script> tại dòng {LineOf(from)} (không parse được: {ex.Message}).");
        }
    }

    private static void Mask(char[] masked, int offset, int from, int to, char with)
    {
        for (var k = from; k < to; k++)
        {
            if (masked[k - offset] is not ('\n' or '\r'))
                masked[k - offset] = with;
        }
    }

    // ---------------------------------------------------------------- '@' transitions

    /// <summary>Đang đứng ở '@' trong markup (hoặc trong giá trị thuộc tính).</summary>
    private void ScanAt(bool inCode, bool inAttribute = false)
    {
        var next = _i + 1 < _t.Length ? _t[_i + 1] : '\0';
        if (next == '*')
        {
            AddComment(_i, IndexAfter("*@", _i + 2));
            return;
        }
        if (next == '@')
        {
            _i += 2;
            return;
        }
        if (next == '(')
        {
            _i++;
            SkipBalancedCSharp();
            return;
        }
        if (next == '{' && !inAttribute)
        {
            _i++;
            ScanCodeBlock();
            return;
        }
        if (next == ':' && !inAttribute)
        {
            _i += 2;
            return; // "@:" — phần còn lại của dòng là markup, cứ để vòng ngoài quét tiếp.
        }
        if (next == '<' && inCode)
        {
            _i++;
            ScanElement();
            return;
        }
        if (!IsIdentStart(next))
        {
            _i++;
            return;
        }

        var word = ReadIdentifier(_i + 1);
        if (!inAttribute && IsAtLineStart(_i) && Directives.Contains(word))
        {
            SkipToEndOfLine();
            return;
        }
        if (!inAttribute && word is "functions" or "code")
        {
            _i += 1 + word.Length;
            SkipWhitespace();
            if (Peek() == '{')
                ScanCodeBlock();
            return;
        }
        if (!inAttribute && word == "section")
        {
            _i += 1 + word.Length;
            SkipWhitespace();
            ReadAndSkipIdentifier();
            SkipWhitespace();
            if (Peek() == '{')
            {
                _i++;
                ScanMarkup(MarkupEnd.Brace, null);
            }
            return;
        }
        if (!inAttribute && word == "using" && IsAtLineStart(_i) && !NextNonSpaceIs(_i + 1 + word.Length, '('))
        {
            SkipToEndOfLine();
            return;
        }
        if (!inAttribute && ControlKeywords.Contains(word))
        {
            _i += 1 + word.Length;
            ScanControlStatement(word);
            return;
        }

        _i++;
        SkipImplicitExpression();
    }

    /// <summary>Sau từ khoá điều khiển (@if, @foreach...): đầu lệnh trong ngoặc, thân khối C#, rồi else/catch/finally.</summary>
    private void ScanControlStatement(string keyword)
    {
        while (true)
        {
            SkipWhitespace();
            if (Peek() == '(')
            {
                SkipBalancedCSharp();
                SkipWhitespace();
            }
            if (Peek() != '{')
                return;
            ScanCodeBlock();

            var save = _i;
            SkipWhitespace();
            var word = IsIdentStart(Peek()) ? ReadIdentifier(_i) : "";
            if (word is "else" or "catch" or "finally" || (keyword == "do" && word == "while"))
            {
                _i += word.Length;
                SkipWhitespace();
                if (word == "else" && At("if"))
                    _i += 2;
                if (keyword == "do" && word == "while")
                {
                    SkipWhitespace();
                    if (Peek() == '(')
                        SkipBalancedCSharp();
                    return;
                }
                continue;
            }
            _i = save;
            return;
        }
    }

    // ---------------------------------------------------------------- C#

    /// <summary>Đang đứng ở '{' mở một khối C#: quét tới '}' tương ứng.</summary>
    private void ScanCodeBlock()
    {
        _i++; // '{'
        var depth = 1;
        var lastSignificant = '{';
        while (_i < _t.Length)
        {
            var c = _t[_i];
            var skipped = SkipCSharpLiteralOrComment();
            if (skipped != Skipped.None)
            {
                // Comment không đổi ngữ cảnh: "<div>" ngay dòng sau một comment vẫn là markup.
                if (skipped == Skipped.Literal)
                    lastSignificant = 'x';
                continue;
            }
            if (c == '@' && _i + 1 < _t.Length && _t[_i + 1] == ':')
            {
                SkipToEndOfLine(); // "@:" — cả dòng là markup
                lastSignificant = ';';
                continue;
            }
            if (c == '@')
            {
                ScanAt(inCode: true);
                lastSignificant = ';';
                continue;
            }
            if (c == '<' && lastSignificant is '{' or '}' or ';' or ':' &&
                _i + 1 < _t.Length && (char.IsLetter(_t[_i + 1]) || _t[_i + 1] == '!'))
            {
                if (At("<!--"))
                    AddComment(_i, IndexAfter("-->", _i + 4));
                else
                    ScanElement();
                lastSignificant = ';';
                continue;
            }
            if (c == '{')
                depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    _i++;
                    return;
                }
            }
            if (!char.IsWhiteSpace(c))
                lastSignificant = c;
            _i++;
        }
    }

    /// <summary>Đang đứng ở '(' hoặc '[': bỏ qua tới ngoặc đóng tương ứng (biết chuỗi/comment C#).</summary>
    private void SkipBalancedCSharp()
    {
        var depth = 0;
        while (_i < _t.Length)
        {
            if (SkipCSharpLiteralOrComment() != Skipped.None)
                continue;
            var c = _t[_i];
            if (c == '@' && _i + 1 < _t.Length && _t[_i + 1] == '<')
            {
                _i++;
                ScanElement();
                continue;
            }
            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth <= 0)
                {
                    _i++;
                    return;
                }
            }
            _i++;
        }
    }

    /// <summary>Biểu thức ngầm: <c>Model.Items[0].Name</c>, <c>Html.Raw(x)</c>, <c>await Foo()</c>.</summary>
    private void SkipImplicitExpression()
    {
        var word = ReadIdentifier(_i);
        _i += word.Length;
        if (word == "await")
        {
            SkipWhitespace();
            if (IsIdentStart(Peek()))
            {
                _i += ReadIdentifier(_i).Length;
            }
        }
        while (_i < _t.Length)
        {
            var c = _t[_i];
            if (c is '(' or '[')
            {
                SkipBalancedCSharp();
            }
            else if (c == '.' && _i + 1 < _t.Length && IsIdentStart(_t[_i + 1]))
            {
                _i++;
                _i += ReadIdentifier(_i).Length;
            }
            else if (c == '?' && _i + 2 < _t.Length && _t[_i + 1] == '.' && IsIdentStart(_t[_i + 2]))
            {
                _i += 2;
                _i += ReadIdentifier(_i).Length;
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>
    /// Nếu đang đứng ở đầu một chuỗi/ký tự C# hoặc comment C#: bỏ qua (comment thì ghi nhận) và cho biết đã bỏ qua gì.
    /// </summary>
    private enum Skipped { None, Literal, Comment }

    private Skipped SkipCSharpLiteralOrComment()
    {
        var c = _t[_i];
        if (c == '/' && _i + 1 < _t.Length && _t[_i + 1] == '/')
        {
            var end = _i;
            while (end < _t.Length && _t[end] != '\n')
                end++;
            AddComment(_i, end);
            return Skipped.Comment;
        }
        if (c == '/' && _i + 1 < _t.Length && _t[_i + 1] == '*')
        {
            AddComment(_i, IndexAfter("*/", _i + 2));
            return Skipped.Comment;
        }
        if (c == '@' && _i + 1 < _t.Length && _t[_i + 1] == '*')
        {
            AddComment(_i, IndexAfter("*@", _i + 2));
            return Skipped.Comment;
        }
        if (c == '\'')
        {
            _i++;
            while (_i < _t.Length && _t[_i] != '\'' && _t[_i] != '\n')
                _i += _t[_i] == '\\' ? 2 : 1;
            _i++;
            return Skipped.Literal;
        }

        // Tiền tố chuỗi: $, @, $@, @$, $$...
        var p = _i;
        var interpolated = false;
        var verbatim = false;
        while (p < _t.Length && (_t[p] == '$' || _t[p] == '@'))
        {
            if (_t[p] == '$') interpolated = true;
            else verbatim = true;
            p++;
        }
        if (p >= _t.Length || _t[p] != '"')
            return Skipped.None;

        if (At("\"\"\"", p))
        {
            // Raw string literal: kết thúc ở một dãy dấu nháy cùng độ dài.
            var q = p;
            while (q < _t.Length && _t[q] == '"') q++;
            var quotes = new string('"', q - p);
            var close = _t.IndexOf(quotes, q, StringComparison.Ordinal);
            _i = close < 0 ? _t.Length : close + quotes.Length;
            return Skipped.Literal;
        }

        _i = p + 1;
        while (_i < _t.Length)
        {
            var ch = _t[_i];
            if (verbatim)
            {
                if (ch == '"')
                {
                    if (_i + 1 < _t.Length && _t[_i + 1] == '"') { _i += 2; continue; }
                    _i++;
                    return Skipped.Literal;
                }
            }
            else
            {
                if (ch == '\\') { _i += 2; continue; }
                if (ch == '"') { _i++; return Skipped.Literal; }
                if (ch == '\n') return Skipped.Literal;
            }
            if (interpolated && ch == '{')
            {
                if (_i + 1 < _t.Length && _t[_i + 1] == '{') { _i += 2; continue; }
                SkipBalancedCSharp();
                continue;
            }
            _i++;
        }
        return Skipped.Literal;
    }

    // ---------------------------------------------------------------- helpers

    private void AddComment(int start, int end)
    {
        if (!CSharpCommentStripper.ShouldKeep(_t[start..end], _keep))
            _spans.Add(new CommentSpan(start, end));
        _i = end;
    }

    private int IndexAfter(string token, int from)
    {
        var idx = _t.IndexOf(token, Math.Min(from, _t.Length), StringComparison.Ordinal);
        return idx < 0 ? _t.Length : idx + token.Length;
    }

    private bool At(string s) => At(s, _i);

    private bool At(string s, int pos) =>
        pos + s.Length <= _t.Length && string.CompareOrdinal(_t, pos, s, 0, s.Length) == 0;

    private char Peek() => _i < _t.Length ? _t[_i] : '\0';

    private void SkipWhitespace()
    {
        while (_i < _t.Length && char.IsWhiteSpace(_t[_i]))
            _i++;
    }

    private void SkipToEndOfLine()
    {
        while (_i < _t.Length && _t[_i] != '\n')
            _i++;
    }

    private void ReadAndSkipIdentifier() => _i += ReadIdentifier(_i).Length;

    private bool NextNonSpaceIs(int pos, char c)
    {
        while (pos < _t.Length && _t[pos] is ' ' or '\t')
            pos++;
        return pos < _t.Length && _t[pos] == c;
    }

    private bool IsAtLineStart(int pos)
    {
        for (var k = pos - 1; k >= 0 && _t[k] != '\n'; k--)
        {
            if (_t[k] is not (' ' or '\t' or '\r'))
                return false;
        }
        return true;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    private string ReadIdentifier(int pos)
    {
        var end = pos;
        while (end < _t.Length && (char.IsLetterOrDigit(_t[end]) || _t[end] == '_'))
            end++;
        return _t[pos..end];
    }

    private string ReadName(int pos)
    {
        var end = pos;
        while (end < _t.Length && (char.IsLetterOrDigit(_t[end]) || _t[end] is '-' or ':' or '_' or '.'))
            end++;
        return _t[pos..end];
    }

    private int LineOf(int pos) => _t.AsSpan(0, pos).Count('\n') + 1;

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
