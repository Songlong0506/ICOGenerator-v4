using System.Text;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Rút các CHUỖI VĂN BẢN ra khỏi mã JavaScript của một mockup HTML — nhãn menu, tiêu đề màn hình, thông báo,
/// câu giải thích luật, giá trị dữ liệu mẫu — bỏ phần mã. Dùng cho <see cref="HtmlDocumentTextExtractor"/>.
///
/// <para>
/// Vì sao không gửi nguyên mã: mockup dựng bằng JS để phần nghiệp vụ quý nhất ở CUỐI script (khối render
/// gán tiêu đề màn, danh sách menu, thông báo), sau hàng chục nghìn ký tự mảng dữ liệu mẫu. Trần chữ mỗi
/// nguồn (<c>Llm:SourceUpload:MaxTextCharsPerFile</c>) cắt từ ĐẦU nên cái tới được BA là mảng dữ liệu, còn
/// tên màn hình thì không. Các chuỗi đã khử trùng chỉ còn cỡ một phần tám mã và đủ để BA đọc ra màn hình,
/// thao tác, trạng thái và luật; mã nguyên văn vẫn đi SAU nó cho model nào có ngân sách rộng hơn.
/// </para>
///
/// <para>
/// Bộ tách từ tối giản, không phải parser JS: nhận biết chú thích, chuỗi <c>'…'</c>/<c>"…"</c>, template
/// literal (phần tĩnh ghép lại, <c>${…}</c> thành "…" và chuỗi BÊN TRONG biểu thức vẫn được lấy) và biểu
/// thức chính quy (theo ký tự đứng trước, như trình duyệt). Chuỗi thường KHÔNG được vắt qua dòng, nên mọi
/// chỗ đoán sai (dấu nháy trong chữ JSX, <c>&lt;/div&gt;</c>) chỉ làm hỏng tới hết DÒNG đó chứ không lan ra
/// cả phần còn lại của script.
/// </para>
/// </summary>
internal static class ScriptTextReader
{
    private const int MaxStringChars = 300;
    // Template lồng template lồng template…: quá mức này thì đọc phẳng, không đệ quy nữa (chặn tràn stack).
    private const int MaxNesting = 32;

    private static readonly string[] RegexPrecedingWords =
        { "return", "typeof", "case", "in", "of", "new", "delete", "void", "throw", "else", "do", "yield", "await" };

    // Giá trị CSS viết trong JS (style inline, màu, kích thước) — nhiều nhất trong mockup và vô nghĩa với BA.
    private static readonly Regex CssProperty = new(
        @"(^|;)\s*(background|border|color|display|font|margin|padding|width|height|min-|max-|gap|flex|grid|position|top|left|right|bottom|inset|align|justify|place-|text-|line-height|opacity|transform|transition|animation|cursor|overflow|box-shadow|z-index|white-space|letter-spacing|outline|fill|stroke)[a-z-]*\s*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CssValue = new(
        @"var\(--|rgba?\(|hsla?\(|calc\(|repeat\(|minmax\(|^#[0-9a-f]{3,8}$|^-?[\d.]+(px|em|rem|%|vh|vw|s|ms|deg|fr)?$"
        + @"|^(transparent|inherit|nowrap|pointer|absolute|relative|inline-block|inline-flex|flex-start|flex-end|space-between|space-around|uppercase|lowercase|ellipsis)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Danh sách class (Tailwind/Bootstrap): nhiều token, toàn chữ thường, có gạch nối.
    private static readonly Regex ClassToken = new(@"^[a-z0-9:/\[\]().%#_!-]+$", RegexOptions.Compiled);
    private static readonly Regex Selector = new(
        @"^([.#\[]\S*|(div|span|section|button|input|a|li|ul|ol|tr|td|th|table|form|select|option|p|h[1-6]|img|label|nav|main|header|footer|aside|article)[.#\[:]\S*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Tag = new(@"<[^<>]*>", RegexOptions.Compiled);

    /// <summary>
    /// Các dòng chữ của một script: mỗi dòng là những chuỗi MỚI nằm trên cùng một dòng mã, nối bằng " · " —
    /// giữ được nhóm "một bản ghi dữ liệu mẫu" hay "một mục menu" mà không phải hiểu cú pháp. Chuỗi đã có
    /// trong <paramref name="seen"/> (từ script trước hay dòng trước) bị lược: giá trị lặp của mảng dữ liệu
    /// là thứ tốn chỗ nhất và không nói thêm gì.
    /// </summary>
    public static List<string> ReadLines(string code, HashSet<string> seen)
    {
        var literals = new List<(int Line, string Text)>();
        var pos = 0;
        var line = 0;
        LexCode(code, ref pos, ref line, stopAtBrace: false, literals, nesting: 0);

        var lines = new List<string>();
        var group = new List<string>();
        var groupLine = -1;
        foreach (var (ln, raw) in literals)
        {
            var text = Normalize(raw);
            if (IsNoise(text) || !seen.Add(text))
                continue;
            if (ln != groupLine && group.Count > 0)
            {
                lines.Add(string.Join(" · ", group));
                group.Clear();
            }
            groupLine = ln;
            group.Add(text);
        }
        if (group.Count > 0)
            lines.Add(string.Join(" · ", group));
        return lines;
    }

    private static void LexCode(
        string code, ref int pos, ref int line, bool stopAtBrace, List<(int Line, string Text)> literals, int nesting)
    {
        var depth = 0;
        var prev = '\0';      // ký tự có nghĩa đứng trước (bỏ khoảng trắng/chú thích); 'a' = định danh/giá trị
        var prevWord = string.Empty;

        while (pos < code.Length)
        {
            var c = code[pos];
            var next = pos + 1 < code.Length ? code[pos + 1] : '\0';

            if (c == '\n')
            {
                line++;
                pos++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                pos++;
                continue;
            }
            if (c == '/' && next == '/')
            {
                var end = code.IndexOf('\n', pos);
                pos = end < 0 ? code.Length : end;
                continue;
            }
            if (c == '/' && next == '*')
            {
                var end = code.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                var stop = end < 0 ? code.Length : end + 2;
                line += CountNewlines(code, pos, stop);
                pos = stop;
                continue;
            }
            if (c is '\'' or '"')
            {
                var start = line;
                literals.Add((start, ReadQuoted(code, ref pos, c)));
                prev = 'a';
                prevWord = string.Empty;
                continue;
            }
            if (c == '`')
            {
                ReadTemplate(code, ref pos, ref line, literals, nesting);
                prev = 'a';
                prevWord = string.Empty;
                continue;
            }
            if (c == '/' && RegexAllowed(prev, prevWord))
            {
                SkipRegex(code, ref pos);
                prev = 'a';
                prevWord = string.Empty;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c is '_' or '$')
            {
                var start = pos;
                while (pos < code.Length && (char.IsLetterOrDigit(code[pos]) || code[pos] is '_' or '$'))
                    pos++;
                prevWord = code[start..pos];
                prev = 'a';
                continue;
            }

            if (c == '{')
                depth++;
            else if (c == '}')
            {
                if (depth == 0 && stopAtBrace)
                {
                    pos++;
                    return;
                }
                depth--;
            }
            prev = c;
            prevWord = string.Empty;
            pos++;
        }
    }

    // Chuỗi '…' / "…". Dừng ở xuống dòng chưa escape: JS không cho chuỗi thường vắt dòng, nên đây là chỗ
    // bộ tách tự hồi phục khi lỡ coi một dấu nháy trong chữ (JSX "Don't") là mở chuỗi.
    private static string ReadQuoted(string code, ref int pos, char quote)
    {
        var sb = new StringBuilder();
        pos++;
        while (pos < code.Length)
        {
            var c = code[pos];
            if (c == quote)
            {
                pos++;
                break;
            }
            if (c == '\n')
                break; // không tiêu thụ — vòng ngoài đếm dòng.
            if (c == '\\' && pos + 1 < code.Length)
            {
                ReadEscape(code, ref pos, sb);
                continue;
            }
            sb.Append(c);
            pos++;
        }
        return sb.ToString();
    }

    // Template literal: phần tĩnh ghép lại, mỗi ${…} thành "…"; chuỗi nằm TRONG biểu thức (vd ${ok ? 'Đạt'
    // : 'Trượt'}) được đọc như mã thường. Giữ chỗ trước khi đọc biểu thức để thứ tự chuỗi vẫn theo mã.
    private static void ReadTemplate(
        string code, ref int pos, ref int line, List<(int Line, string Text)> literals, int nesting)
    {
        var startLine = line;
        var index = literals.Count;
        literals.Add((startLine, string.Empty));
        var sb = new StringBuilder();
        pos++;
        while (pos < code.Length)
        {
            var c = code[pos];
            if (c == '`')
            {
                pos++;
                break;
            }
            if (c == '\\' && pos + 1 < code.Length)
            {
                ReadEscape(code, ref pos, sb);
                continue;
            }
            if (c == '$' && pos + 1 < code.Length && code[pos + 1] == '{' && nesting < MaxNesting)
            {
                pos += 2;
                sb.Append(" … ");
                LexCode(code, ref pos, ref line, stopAtBrace: true, literals, nesting + 1);
                continue;
            }
            if (c == '\n')
                line++;
            sb.Append(c);
            pos++;
        }
        literals[index] = (startLine, sb.ToString());
    }

    private static void SkipRegex(string code, ref int pos)
    {
        pos++;
        var inClass = false;
        while (pos < code.Length)
        {
            var c = code[pos];
            if (c == '\n')
                return;
            if (c == '\\')
            {
                pos += 2;
                continue;
            }
            if (c == '[')
                inClass = true;
            else if (c == ']')
                inClass = false;
            else if (c == '/' && !inClass)
            {
                pos++;
                while (pos < code.Length && char.IsLetter(code[pos]))
                    pos++; // cờ g/i/m/u/y/s
                return;
            }
            pos++;
        }
    }

    // Dấu '/' là mở biểu thức chính quy khi đứng ở chỗ một GIÁ TRỊ được chờ (sau toán tử, dấu mở ngoặc,
    // từ khoá như return); sau định danh hay ')' thì nó là phép chia.
    private static bool RegexAllowed(char prev, string prevWord) =>
        prevWord.Length > 0
            ? RegexPrecedingWords.Contains(prevWord, StringComparer.Ordinal)
            : prev == '\0' || "(,=:[!&|?{};+-*%<>~^".Contains(prev);

    // Escape trong chuỗi, pos đang ở dấu '\'. \uXXXX / \u{…} / \xXX phải giải mã: mockup (nhất là bản xuất từ
    // công cụ) hay viết chữ tiếng Việt dạng đó, đọc thô thì "Tr\u1ea1ng th\u00e1i" thành rác.
    private static void ReadEscape(string code, ref int pos, StringBuilder sb)
    {
        var kind = code[pos + 1];
        pos += 2;
        switch (kind)
        {
            case 'n' or 'r' or 't':
                sb.Append(' ');
                return;
            case 'u' when pos < code.Length && code[pos] == '{':
                {
                    var close = code.IndexOf('}', pos);
                    if (close > pos && close - pos <= 7
                        && int.TryParse(code.AsSpan(pos + 1, close - pos - 1), System.Globalization.NumberStyles.HexNumber, null, out var cp)
                        && cp <= 0x10FFFF && (cp < 0xD800 || cp > 0xDFFF))
                    {
                        sb.Append(char.ConvertFromUtf32(cp));
                        pos = close + 1;
                        return;
                    }
                    break;
                }
            case 'u' or 'x':
                {
                    var digits = kind == 'u' ? 4 : 2;
                    if (pos + digits <= code.Length
                        && int.TryParse(code.AsSpan(pos, digits), System.Globalization.NumberStyles.HexNumber, null, out var value))
                    {
                        sb.Append((char)value);
                        pos += digits;
                        return;
                    }
                    break;
                }
        }
        sb.Append(kind);
    }

    private static int CountNewlines(string code, int from, int to)
    {
        var n = 0;
        for (var i = from; i < to && i < code.Length; i++)
            if (code[i] == '\n')
                n++;
        return n;
    }

    private static string Normalize(string raw)
    {
        var text = HtmlDocumentTextExtractor.Bindings.Replace(Tag.Replace(raw, " "), "⟨$1⟩");
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(ch);
        }
        text = sb.ToString().Trim(' ', '·', '…');
        return text.Length > MaxStringChars ? text[..MaxStringChars] + "…" : text;
    }

    private static bool IsNoise(string text)
    {
        if (text.Length < 2 || !text.Any(char.IsLetter))
            return true;
        if (CssProperty.IsMatch(text) || CssValue.IsMatch(text) || Selector.IsMatch(text))
            return true;
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("//", StringComparison.Ordinal))
            return true;
        var tokens = text.Split(' ');
        return tokens.Length >= 2 && tokens.All(t => ClassToken.IsMatch(t)) && tokens.Any(t => t.Contains('-'));
    }
}
