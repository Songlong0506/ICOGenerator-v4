using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace ICOGenerator.Services.Requirements;

/// <summary>Kết quả bóc một file HTML: text (null nếu không có chữ nào) + số hình nhúng đã lấy ra được.</summary>
public sealed record HtmlExtraction(string? Text, int ImageCount);

/// <summary>
/// Bóc một trang HTML — thường là <b>mockup giao diện</b> người dùng tự dựng hoặc xuất từ công cụ thiết kế —
/// thành text cho BA đọc: tiêu đề, heading (<c>#</c>), đoạn chữ, danh sách, bảng (dòng <c>ô | ô</c> như
/// Word), CONTROL của form (<c>[Ô nhập: …]</c>, <c>[Chọn: a / b]</c>, <c>[Nút: …]</c>, ☐/○), chú thích HTML
/// ngắn (mockup hay đánh dấu từng màn/hộp thoại bằng chúng), cộng các HÌNH NHÚNG đủ lớn ra
/// <c>figure-{n}.*</c> kèm mốc <c>[Hình n]</c> — cùng khuôn với <see cref="WordDocumentTextExtractor"/>.
///
/// <para>
/// Ba điều khiến HTML khác hẳn Word, mỗi điều là một cách đọc mockup ra rỗng:
/// </para>
/// <list type="bullet">
/// <item><b>Không lấy <c>innerText</c> của trình duyệt mà đọc cả phần đang ẨN.</b> Mockup nhiều màn giấu
/// mọi màn trừ màn đầu (<c>display:none</c>, <c>hidden</c>, nhánh điều kiện của template). Đọc như người
/// xem thì BA chỉ thấy đúng một màn; đọc DOM tĩnh thì thấy đủ — và không phải chạy JS nào.</item>
/// <item><b>Script nhúng được đọc</b>, SAU phần giao diện. Mockup dựng bằng React/Vue/Alpine hay template
/// của công cụ thiết kế để nhãn màn hình, dữ liệu mẫu và luồng thao tác nằm TRONG JavaScript, còn DOM tĩnh
/// chỉ là cái khung với <c>{{ binding }}</c> (viết lại thành <c>⟨binding⟩</c>, xem <see cref="Bindings"/>). Bỏ
/// script là bỏ nửa mockup. Thứ tự là thứ tự ưu tiên khi
/// trần chữ cắt từ cuối: giao diện → CHỮ trong script (<see cref="ScriptTextHeading"/>, xem
/// <see cref="ScriptTextReader"/>) → mã nguyên văn (<see cref="ScriptCodeHeading"/>). Thư viện (<c>src</c>
/// ngoài, hoặc mã đã nén một dòng dài) thì bỏ — nó là nhiễu, không phải nghiệp vụ.</item>
/// <item><b>File "tự chứa" (bundled page) được mở gói.</b> Một số công cụ xuất mockup thành MỘT file HTML
/// mà phần thân thật nằm trong <c>&lt;script type="__bundler/template"&gt;</c> (chuỗi JSON), ảnh/font/thư
/// viện nằm trong <c>__bundler/manifest</c> (base64, có thể gzip), trang con nằm trong
/// <c>__bundler/page_order</c>. Đọc vỏ ngoài chỉ ra "Unpacking…" — nên phải mở đúng như loader của nó
/// làm, nhưng không chạy JS.</item>
/// </list>
///
/// <para>
/// Best-effort như các bộ bóc khác: file hỏng/không đọc được ⇒ trả rỗng, caller giữ file gốc. KHÔNG tải
/// tài nguyên ngoài (parser AngleSharp mặc định không có loader) — file do người dùng upload, mở mạng từ
/// server theo URL trong file là cửa SSRF.
/// </para>
/// </summary>
public static class HtmlDocumentTextExtractor
{
    public static readonly string[] Extensions = { ".html", ".htm" };

    /// <summary>Mốc mở khối CHỮ rút từ script — ngay sau phần giao diện.</summary>
    public const string ScriptTextHeading = "#### Chữ trong script của trang";

    /// <summary>Mốc mở khối MÃ script nguyên văn — sau cùng, phần đầu tiên bị trần chữ cắt đi.</summary>
    public const string ScriptCodeHeading = "#### Mã script nhúng trong trang";

    // Trần phần GIAO DIỆN — cùng cỡ với Word: BA cần nắm màn hình, trường, nút, không cần nguyên văn mọi ô.
    private const int MaxTotalChars = 40000;
    // Trần phần CHỮ trong script và phần MÃ script, mỗi phần cộng dồn mọi script. Tách riêng khỏi trần giao
    // diện để một trang nhiều chữ không nuốt chỗ của nhãn/dữ liệu trong JS, và ngược lại.
    private const int MaxScriptTextChars = 12000;
    private const int MaxScriptCodeChars = 20000;
    private const int MaxCharsPerLine = 2000;
    private const int MaxTableRows = 60;
    private const int MaxSelectOptions = 20;
    private const int MaxCommentChars = 150;
    // Script ngắn hơn ngần này là đoạn khởi tạo lặt vặt (gắn sự kiện, gọi init), không chở nghiệp vụ.
    private const int MinScriptChars = 40;
    // Bình quân ký tự mỗi dòng vượt ngần này (với script đủ dài) ⇒ mã đã nén / thư viện đóng gói.
    private const int MinifiedAverageLineChars = 200;

    // Trần độ sâu DOM khi duyệt đệ quy: file do người dùng upload, một chuỗi 100k thẻ lồng nhau không được
    // làm tràn stack (StackOverflow không bắt được — nó giết cả tiến trình web).
    private const int MaxDomDepth = 256;
    // Trần độ sâu trang lồng (iframe srcdoc / trang con của bundle).
    private const int MaxFrameDepth = 3;
    // Trần dung lượng SAU giải nén một asset của bundle — chặn gzip bomb.
    private const int MaxDecodedAssetBytes = 25 * 1024 * 1024;
    // Ngân sách thời gian PARSE cho cả file (trang chính + trang lồng). Bộ dựng cây HTML5 dò "phạm vi" trên
    // ngăn xếp phần tử đang mở ở mỗi thẻ khối, nên thẻ lồng sâu làm nó chạy bậc hai: 5MB "<div><div>…" là
    // hàng chục phút CPU trên luồng request. Mockup thật parse dưới một giây; hết ngân sách ⇒ bỏ phần bóc
    // chữ (file gốc vẫn lưu, người dùng được báo "không đọc được").
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(15);

    // Thẻ không chở chữ người dùng nhìn thấy (hoặc chở mã, xử lý riêng ở khối script).
    private static readonly HashSet<string> SkippedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "head", "meta", "link", "title", "base",
        "svg", "canvas", "object", "embed", "audio", "video", "source", "track", "map", "math",
    };

    // Thẻ khối: kết thúc dòng hiện tại trước và sau nó. Phần tử tuỳ biến (có dấu gạch ngang) cũng coi là
    // khối — framework/công cụ thiết kế bọc từng thành phần trong thẻ riêng của chúng.
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "body", "center", "dd", "details", "dialog", "dir",
        "div", "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form", "header", "hgroup", "html",
        "legend", "main", "menu", "nav", "ol", "p", "pre", "section", "summary", "ul", "tr", "td", "th",
        "caption", "option", "optgroup",
    };

    /// <summary>
    /// Binding của template (<c>{{ k.label }}</c> — Vue/Angular/Handlebars và công cụ thiết kế) viết lại thành
    /// <c>⟨k.label⟩</c>: ngắn hơn, và không bao giờ bị nhầm với placeholder <c>{{…}}</c> của prompt — nhờ vậy
    /// prompt đọc tài liệu nguồn mới gọi tên được nó (xem <c>source-ack.v3.md</c>).
    /// </summary>
    internal static readonly Regex Bindings = new(@"\{\{\s*([^{}\r\n]{1,120}?)\s*\}\}", RegexOptions.Compiled);

    // Kiểu script chở DỮ LIỆU/khai báo, không phải mã giao diện — bỏ qua.
    private static readonly string[] NonCodeScriptTypeMarkers = { "json", "importmap", "speculationrules", "__bundler", "shader" };

    public static bool IsHtmlDocument(string? contentType, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (Extensions.Contains(ext))
            return true;
        var ct = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        return ct == "text/html" || ct == "application/xhtml+xml";
    }

    /// <summary>Bóc text từ bytes, không lấy hình. Trả null nếu không đọc được gì.</summary>
    public static string? Extract(byte[] bytes) => Extract(bytes, imageTargetDir: null).Text;

    /// <summary>
    /// Bóc text + hình nhúng. Hình được ghi thành <c>figure-{n}.png/.jpeg/…</c> vào
    /// <paramref name="imageTargetDir"/> (null ⇒ chỉ bóc text). Trả Text=null nếu trang không có chữ nào,
    /// hoặc parse vượt ngân sách thời gian.
    /// </summary>
    public static HtmlExtraction Extract(byte[] bytes, string? imageTargetDir, CancellationToken cancellationToken = default) =>
        Extract(bytes, imageTargetDir, ParseTimeout, cancellationToken);

    internal static HtmlExtraction Extract(
        byte[] bytes, string? imageTargetDir, TimeSpan parseTimeout, CancellationToken cancellationToken)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(parseTimeout);
            var parser = new BoundedParser(budget.Token);

            IHtmlDocument outer;
            using (var ms = new MemoryStream(bytes))
                outer = parser.Parse(ms); // tự dò charset theo BOM / <meta charset>

            using var assets = new BundleAssets();
            var document = Unwrap(outer, parser, assets);

            var walker = new Walker(parser, assets, collectFigures: imageTargetDir != null);
            var title = Clean(document.Title);
            if (title.Length > 0)
                walker.AddLine($"Tiêu đề trang: {title}");
            walker.WalkDocument(document, frameDepth: 0);
            walker.Flush();

            var lines = walker.Lines;
            var written = imageTargetDir == null ? 0 : WriteFiguresAndInsertMarkers(walker.Figures, lines, imageTargetDir);

            var ui = string.Join("\n", lines).Trim();
            if (ui.Length > MaxTotalChars)
                ui = ui[..MaxTotalChars] + "\n…(đã cắt bớt)";

            var (scriptText, scriptCode) = RenderScripts(walker.Documents);
            var result = string.Join("\n\n", new[] { ui, scriptText, scriptCode }.Where(s => s.Length > 0));
            return new HtmlExtraction(result.Length == 0 ? null : result, written);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // người dùng huỷ request — không phải "file không đọc được".
        }
        catch
        {
            return new HtmlExtraction(null, 0); // best-effort: hỏng/quá ngân sách parse thì bỏ qua, giữ file gốc.
        }
    }

    /// <summary>
    /// Parser gắn ngân sách thời gian. AngleSharp chỉ kiểm <see cref="CancellationToken"/> ở đường async; ở
    /// đây nguồn là chuỗi/bytes trong bộ nhớ (không có I/O thật) và ASP.NET Core không có
    /// SynchronizationContext, nên chờ đồng bộ không kẹt luồng nào.
    /// </summary>
    private sealed class BoundedParser
    {
        private readonly HtmlParser _parser = new();
        private readonly CancellationToken _token;

        public BoundedParser(CancellationToken token) => _token = token;

        public IHtmlDocument Parse(string html) => _parser.ParseDocumentAsync(html, _token).GetAwaiter().GetResult();

        public IHtmlDocument Parse(Stream stream) => _parser.ParseDocumentAsync(stream, _token).GetAwaiter().GetResult();
    }

    // Trang "tự chứa" ⇒ trả về tài liệu THẬT bên trong nó (và nạp manifest vào kho asset); trang thường ⇒
    // trả nguyên. Chỉ mở một lớp: template của bundle là HTML thường.
    private static IHtmlDocument Unwrap(IHtmlDocument document, BoundedParser parser, BundleAssets assets)
    {
        var templateEl = document.QuerySelector("script[type='__bundler/template']");
        if (templateEl == null)
            return document;

        assets.Load(
            document.QuerySelector("script[type='__bundler/manifest']")?.TextContent,
            document.QuerySelector("script[type='__bundler/page_order']")?.TextContent);

        var html = JsonSerializer.Deserialize<string>(templateEl.TextContent.Trim());
        return string.IsNullOrWhiteSpace(html) ? document : parser.Parse(html);
    }

    // ==== Duyệt DOM ====

    private sealed class Walker
    {
        private readonly BoundedParser _parser;
        private readonly BundleAssets _assets;
        private readonly bool _collectFigures;
        private readonly StringBuilder _current = new();
        private readonly HashSet<string> _seenFigureHashes = new(StringComparer.Ordinal);
        private int _totalChars;

        public Walker(BoundedParser parser, BundleAssets assets, bool collectFigures)
        {
            _parser = parser;
            _assets = assets;
            _collectFigures = collectFigures;
        }

        public List<string> Lines { get; } = new();
        public List<FigureCandidate> Figures { get; } = new();

        /// <summary>Mọi tài liệu đã đi qua (trang chính + trang lồng) — khối script đọc lại từ đây.</summary>
        public List<IDocument> Documents { get; } = new();

        private bool Full => _totalChars >= MaxTotalChars;

        // depth của trang lồng nối tiếp độ sâu của khung chứa nó: trần MaxDomDepth là trần của CẢ chồng gọi
        // đệ quy, không phải của từng trang.
        public void WalkDocument(IDocument document, int frameDepth, int depth = 0)
        {
            Documents.Add(document);
            if (document.Body != null)
                WalkChildren(document.Body, depth, frameDepth);
        }

        public void AddLine(string line)
        {
            Flush();
            line = Bindings.Replace(Clean(line), "⟨$1⟩");
            if (line.Length == 0 || line == "-" || (Lines.Count > 0 && Lines[^1] == line))
                return; // mục danh sách rỗng; dòng trùng liền kề (menu lặp, nhãn lặp) chỉ tốn chỗ.
            Lines.Add(line);
            _totalChars += line.Length;
        }

        public void Flush()
        {
            if (_current.Length == 0)
                return;
            var text = _current.ToString();
            _current.Clear();
            AddLine(text);
        }

        private void Inline(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return;
            if (_current.Length > 0 && !char.IsWhiteSpace(_current[^1]))
                _current.Append(' ');
            _current.Append(token);
        }

        private void WalkChildren(INode node, int depth, int frameDepth)
        {
            if (depth > MaxDomDepth)
                return;
            foreach (var child in node.ChildNodes)
            {
                if (Full)
                    return;
                switch (child)
                {
                    case IText text:
                        _current.Append(text.Data);
                        break;
                    case IComment comment:
                        var note = CommentText(comment.Data);
                        if (note != null)
                            AddLine($"[Chú thích: {note}]");
                        break;
                    case IElement element:
                        VisitElement(element, depth + 1, frameDepth);
                        break;
                }
            }
        }

        private void VisitElement(IElement el, int depth, int frameDepth)
        {
            var tag = el.LocalName;
            if (SkippedTags.Contains(tag))
                return;

            switch (tag)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    {
                        var text = Flatten(el, depth);
                        if (text.Length > 0)
                            AddLine(new string('#', tag[1] - '0') + " " + text);
                        return;
                    }
                case "table":
                    Flush();
                    WriteTable(el, depth);
                    return;
                case "li":
                    Flush();
                    _current.Append("- ");
                    WalkChildren(el, depth, frameDepth);
                    Flush();
                    return;
                case "br" or "hr":
                    Flush();
                    return;
                case "input" or "select" or "textarea" or "button":
                    Inline(ControlToken(el, depth));
                    return;
                case "img":
                    VisitImage(el);
                    return;
                case "iframe" or "frame":
                    Flush();
                    VisitFrame(el, depth, frameDepth);
                    return;
                case "template":
                    // Nội dung <template> nằm ở fragment riêng chứ không phải con trong cây (Vue/Alpine hay dựng
                    // cả màn hình trong đó).
                    if (el is IHtmlTemplateElement template)
                        WalkChildren(template.Content, depth, frameDepth);
                    return;
            }

            // Phần tử inline vẫn được ngăn bằng một khoảng trắng: không biết CSS thì không biết con của một
            // lưới/flex là từng ô riêng, và "TypeROPSafety" dính chữ tệ hơn nhiều một dấu cách thừa.
            var block = BlockTags.Contains(tag) || tag.Contains('-');
            if (block)
                Flush();
            else
                _current.Append(' ');
            WalkChildren(el, depth, frameDepth);
            if (block)
                Flush();
            else
                _current.Append(' ');
        }

        // Bảng render như Word: mỗi hàng một dòng "ô | ô | ô". Chỉ lấy hàng của CHÍNH bảng này (bảng lồng
        // trong ô được làm phẳng vào ô đó), duyệt tay thay vì QuerySelectorAll để giữ trần độ sâu.
        private void WriteTable(IElement table, int depth)
        {
            var caption = table.Children.FirstOrDefault(c => c.LocalName == "caption");
            if (caption != null)
                AddLine($"Bảng: {Flatten(caption, depth)}");

            var rows = table.Children
                .SelectMany(c => c.LocalName is "thead" or "tbody" or "tfoot" ? c.Children : (IEnumerable<IElement>)new[] { c })
                .Where(r => r.LocalName == "tr")
                .ToList();

            var written = 0;
            foreach (var row in rows)
            {
                if (Full)
                    return;
                if (written >= MaxTableRows)
                {
                    AddLine($"…(còn {rows.Count - written} dòng nữa của bảng này)");
                    break;
                }
                var cells = row.Children
                    .Where(c => c.LocalName is "td" or "th")
                    .Select(c => Flatten(c, depth + 1))
                    .ToList();
                var line = string.Join(" | ", cells).Trim();
                if (line.Replace("|", string.Empty).Trim().Length == 0)
                    continue;
                AddLine(line);
                written++;
            }
            if (written > 0)
            {
                Lines.Add(string.Empty); // ngắt bảng khỏi phần sau, như Word.
            }
        }

        private void VisitImage(IElement img)
        {
            var alt = Clean(img.GetAttribute("alt") ?? img.GetAttribute("title"));
            if (_collectFigures)
            {
                var candidate = TryFigure(img.GetAttribute("src"));
                if (candidate != null)
                {
                    Flush();
                    Figures.Add(candidate with { LineIndex = Lines.Count });
                }
            }
            if (alt.Length > 0)
                Inline($"[Ảnh: {alt}]");
        }

        private FigureCandidate? TryFigure(string? src)
        {
            var (bytes, mime) = _assets.ReadImage(src);
            if (bytes == null || bytes.Length == 0)
                return null;
            var ext = WordDocumentTextExtractor.ExtensionFor(mime ?? string.Empty);
            if (ext == null)
                return null;
            var pixels = WordDocumentTextExtractor.TryReadPixelCount(bytes);
            var keep = pixels > 0
                ? pixels >= WordDocumentTextExtractor.MinImagePixels
                : bytes.Length >= WordDocumentTextExtractor.MinBytesWhenSizeUnknown;
            if (!keep || !_seenFigureHashes.Add(Convert.ToHexString(SHA256.HashData(bytes))))
                return null; // icon/trang trí, hoặc cùng một ảnh (logo) lặp lại.
            return new FigureCandidate(bytes, ext, pixels, 0);
        }

        // Trang lồng: iframe srcdoc, hoặc trang con của bundle (src = "about:blank#{uuid}"). Nội dung của nó
        // là một phần của mockup (khung xem trước, màn hình con) nên đọc tại chỗ, giữa hai mốc.
        private void VisitFrame(IElement frame, int depth, int frameDepth)
        {
            if (frameDepth >= MaxFrameDepth)
                return;

            var html = frame.GetAttribute("srcdoc");
            if (string.IsNullOrWhiteSpace(html))
                html = _assets.PageText(frame.GetAttribute("src"));
            if (string.IsNullOrWhiteSpace(html))
                return;

            var nested = Unwrap(_parser.Parse(html), _parser, _assets);
            var name = Clean(frame.GetAttribute("title") ?? frame.GetAttribute("name") ?? nested.Title);
            AddLine(name.Length > 0 ? $"--- Khung nhúng: {name} ---" : "--- Khung nhúng ---");
            WalkDocument(nested, frameDepth + 1, depth);
            Flush();
            AddLine("--- Hết khung nhúng ---");
        }

        // Chữ của một cây con trên MỘT dòng — cho heading, ô bảng, nhãn nút. Control bên trong vẫn hiện
        // thành token (ô bảng chứa nút "Sửa" là thông tin), khối con chỉ còn là khoảng trắng.
        private string Flatten(INode node, int depth)
        {
            var sb = new StringBuilder();
            FlattenInto(node, sb, depth);
            return Clean(sb.ToString());
        }

        private void FlattenInto(INode node, StringBuilder sb, int depth)
        {
            if (depth > MaxDomDepth)
                return;
            foreach (var child in node.ChildNodes)
            {
                switch (child)
                {
                    case IText text:
                        sb.Append(text.Data);
                        break;
                    case IElement el when SkippedTags.Contains(el.LocalName):
                        break;
                    case IElement el when el.LocalName is "input" or "select" or "textarea" or "button":
                        sb.Append(' ').Append(ControlToken(el, depth + 1)).Append(' ');
                        break;
                    case IElement el when el.LocalName == "img":
                        var alt = Clean(el.GetAttribute("alt"));
                        if (alt.Length > 0)
                            sb.Append($" [Ảnh: {alt}] ");
                        break;
                    case IElement el:
                        sb.Append(' ');
                        FlattenInto(el, sb, depth + 1);
                        sb.Append(' ');
                        break;
                }
            }
        }

        // Control của form thành token đọc được: loại control + nhãn gợi ý (placeholder/aria-label/title/
        // name) + giá trị mẫu. Đây là phần quý nhất của một mockup form — nó chính là danh sách TRƯỜNG.
        private string? ControlToken(IElement el, int depth)
        {
            switch (el.LocalName)
            {
                case "button":
                    {
                        var label = Flatten(el, depth);
                        if (label.Length == 0)
                            label = Hint(el);
                        return label.Length > 0 ? $"[Nút: {label}]" : null; // nút chỉ có icon, không nhãn: bỏ.
                    }
                case "select":
                    {
                        var options = el.Children
                            .SelectMany(c => c.LocalName == "optgroup" ? c.Children : (IEnumerable<IElement>)new[] { c })
                            .Where(o => o.LocalName == "option")
                            .Select(o => Flatten(o, depth + 1))
                            .Where(o => o.Length > 0)
                            .ToList();
                        var hint = Hint(el);
                        var head = hint.Length > 0 ? $"Chọn ({hint})" : "Chọn";
                        if (options.Count == 0)
                            return $"[{head}]";
                        var shown = string.Join(" / ", options.Take(MaxSelectOptions));
                        var more = options.Count > MaxSelectOptions ? $" / …(+{options.Count - MaxSelectOptions})" : string.Empty;
                        return $"[{head}: {shown}{more}]";
                    }
                case "textarea":
                    {
                        var hint = Hint(el);
                        var value = Clean(el.TextContent);
                        return Token("Ô nhập nhiều dòng", hint, value);
                    }
            }

            var type = (el.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();
            switch (type)
            {
                case "hidden":
                    return null;
                case "checkbox":
                    return el.HasAttribute("checked") ? "☑" : "☐";
                case "radio":
                    return el.HasAttribute("checked") ? "◉" : "○";
                case "submit" or "button" or "reset" or "image":
                    {
                        var label = Clean(el.GetAttribute("value"));
                        if (label.Length == 0)
                            label = Hint(el);
                        return label.Length > 0 ? $"[Nút: {label}]" : null;
                    }
                case "file":
                    return Token("Chọn tệp", Hint(el), string.Empty);
                default:
                    {
                        var kind = type is "text" or "" ? "Ô nhập" : $"Ô nhập {type}";
                        return Token(kind, Hint(el), Clean(el.GetAttribute("value")));
                    }
            }
        }
    }

    private static string? Token(string kind, string hint, string value)
    {
        var sb = new StringBuilder("[").Append(kind);
        if (hint.Length > 0)
            sb.Append(": ").Append(hint);
        if (value.Length > 0)
            sb.Append(hint.Length > 0 ? " = " : ": ").Append(value);
        return sb.Append(']').ToString();
    }

    private static string Hint(IElement el)
    {
        foreach (var attr in new[] { "placeholder", "aria-label", "title", "name" })
        {
            var value = Clean(el.GetAttribute(attr));
            if (value.Length > 0)
                return value;
        }
        return string.Empty;
    }

    // Chú thích HTML NGẮN thường là nhãn đoạn do người dựng mockup viết ("===== DASHBOARD =====",
    // "TRANSFER DIALOG") — đúng thứ BA cần để biết đoạn nào là màn hình nào. Chú thích dài (bản quyền,
    // khối tắt code) và chú thích điều kiện của IE thì bỏ.
    private static string? CommentText(string? raw)
    {
        var text = Clean(raw).Trim('=', '-', '*', '#', '~', '_', '/', ' ');
        if (text.Length == 0 || text.Length > MaxCommentChars || text.StartsWith("[if", StringComparison.OrdinalIgnoreCase)
            || text.Contains('<'))
            return null;
        return text;
    }

    // ==== Script nhúng ====

    // Hai khối từ cùng các script: CHỮ (nhãn, thông báo, dữ liệu — gọn, đứng trước) và MÃ nguyên văn (đứng
    // sau, cho model có ngân sách rộng đọc được cả điều kiện/luật viết bằng code). Trả chuỗi rỗng khi không có.
    private static (string Text, string Code) RenderScripts(IEnumerable<IDocument> documents)
    {
        var textLines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var textChars = 0;
        var textTruncated = false;

        var codeBlocks = new List<string>();
        var codeChars = 0;
        var codeTruncated = false;
        var skippedLibraries = 0;

        foreach (var script in documents.SelectMany(d => d.QuerySelectorAll("script")))
        {
            if (script.HasAttribute("src"))
                continue; // thư viện/mã ngoài: không có ở đây, và không phải nghiệp vụ.
            var type = (script.GetAttribute("type") ?? string.Empty).Trim().ToLowerInvariant();
            if (NonCodeScriptTypeMarkers.Any(m => type.Contains(m, StringComparison.Ordinal)))
                continue;

            var code = script.TextContent;
            if (code.Trim().Length < MinScriptChars)
                continue;
            if (LooksMinified(code))
            {
                skippedLibraries++;
                continue;
            }

            foreach (var line in ScriptTextReader.ReadLines(code, seen))
            {
                if (textChars + line.Length > MaxScriptTextChars)
                {
                    textTruncated = true;
                    break;
                }
                textLines.Add(line);
                textChars += line.Length + 1;
            }

            if (codeChars >= MaxScriptCodeChars)
            {
                codeTruncated = true;
                continue;
            }
            var compact = CompactCode(code);
            var room = MaxScriptCodeChars - codeChars;
            if (compact.Length > room)
            {
                compact = compact[..room];
                codeTruncated = true;
            }
            var label = type.Length > 0 ? $" ({type})" : string.Empty;
            codeBlocks.Add($"--- Script {codeBlocks.Count + 1}{label} ---\n{compact}");
            codeChars += compact.Length;
        }

        var text = new StringBuilder();
        if (textLines.Count > 0)
        {
            text.AppendLine(ScriptTextHeading);
            text.AppendLine("(Các chuỗi văn bản viết trong script của mockup — nhãn menu, tiêu đề màn hình, nút, thông "
                + "báo, câu giải thích luật, giá trị dữ liệu mẫu — những chỗ ⟨…⟩ ở phần giao diện được điền từ "
                + "đây. Mỗi dòng là các chuỗi nằm trên cùng một dòng mã; chuỗi lặp lại đã được lược.)");
            text.Append(string.Join("\n", textLines));
            if (textTruncated)
                text.Append("\n…(đã cắt bớt)");
        }

        var codeText = new StringBuilder();
        if (codeBlocks.Count > 0)
        {
            codeText.AppendLine(ScriptCodeHeading);
            codeText.AppendLine("(Mã JavaScript viết tay trong mockup, đã bỏ thụt lề — đọc khi cần điều kiện/luật mà phần "
                + "chữ ở trên không nói hết.)");
            if (skippedLibraries > 0)
                codeText.AppendLine($"(Đã bỏ qua {skippedLibraries} script dạng thư viện/đã nén.)");
            codeText.Append(string.Join("\n\n", codeBlocks));
            if (codeTruncated)
                codeText.Append("\n…(phần mã còn lại đã bị cắt bớt)");
        }

        return (text.ToString().TrimEnd(), codeText.ToString().TrimEnd());
    }

    private static bool LooksMinified(string code)
    {
        if (code.Length < 2000)
            return false;
        var lines = code.Count(c => c == '\n') + 1;
        return code.Length / lines > MinifiedAverageLineChars;
    }

    // Bỏ thụt đầu dòng và dòng trống: với ngân sách chữ tính theo ký tự, thụt lề của mã lồng sâu ăn tới
    // một phần ba khối script mà model không cần nó để đọc.
    private static string CompactCode(string code) => string.Join("\n", code
        .Replace("\r", string.Empty)
        .Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0));

    // ==== Hình nhúng ====

    private sealed record FigureCandidate(byte[] Bytes, string Extension, long Pixels, int LineIndex);

    // Như Word: vượt trần thì GIỮ các hình lớn nhất, đánh số theo thứ tự trang, ghi figure-{n}.{ext} và
    // chèn mốc "[Hình n]" từ dưới lên để chỉ số dòng phía trên không lệch.
    private static int WriteFiguresAndInsertMarkers(List<FigureCandidate> candidates, List<string> lines, string imageTargetDir)
    {
        if (candidates.Count == 0)
            return 0;

        var selected = candidates.Count <= WordDocumentTextExtractor.MaxImages
            ? candidates
            : candidates
                .Select((c, i) => (c, i))
                .OrderByDescending(x => x.c.Pixels > 0 ? x.c.Pixels : x.c.Bytes.Length)
                .Take(WordDocumentTextExtractor.MaxImages)
                .OrderBy(x => x.i)
                .Select(x => x.c)
                .ToList();

        var written = 0;
        var markers = new List<(int LineIndex, string Marker)>();
        foreach (var figure in selected)
        {
            try
            {
                var n = written + 1;
                File.WriteAllBytes(
                    Path.Combine(imageTargetDir, $"{WordDocumentTextExtractor.FigureImagePrefix}{n}.{figure.Extension}"),
                    figure.Bytes);
                markers.Add((figure.LineIndex, $"[Hình {n} — ảnh nhúng trong trang, gửi kèm dưới dạng ảnh]"));
                written++;
            }
            catch
            {
                // Ghi file lỗi (đĩa đầy…): bỏ qua hình đó.
            }
        }

        for (var i = markers.Count - 1; i >= 0; i--)
            lines.Insert(Math.Min(markers[i].LineIndex, lines.Count), markers[i].Marker);
        return written;
    }

    // ==== Asset của trang tự chứa ====

    /// <summary>
    /// Kho asset của bundle (manifest: uuid → {mime, compressed, data base64}) + text các trang con, và bộ
    /// đọc ảnh <c>data:</c> URI cho trang thường. Giải mã LƯỜI — chỉ asset thật sự được tham chiếu — vì
    /// phần lớn manifest là font và thư viện JS (vài MB) mà ta không bao giờ cần.
    /// </summary>
    private sealed class BundleAssets : IDisposable
    {
        private readonly List<JsonDocument> _manifests = new();
        private readonly Dictionary<string, JsonElement> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pages = new(StringComparer.OrdinalIgnoreCase);

        public void Load(string? manifestJson, string? pageOrderJson)
        {
            if (!string.IsNullOrWhiteSpace(manifestJson))
            {
                var doc = JsonDocument.Parse(manifestJson);
                _manifests.Add(doc);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        _entries.TryAdd(prop.Name, prop.Value);
            }
            if (!string.IsNullOrWhiteSpace(pageOrderJson)
                && JsonSerializer.Deserialize<string[]>(pageOrderJson) is { } pages)
                _pages.UnionWith(pages);
        }

        /// <summary>Text HTML của trang con bundle mà iframe trỏ tới bằng "about:blank#{uuid}".</summary>
        public string? PageText(string? src)
        {
            const string marker = "about:blank#";
            if (src == null || !src.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                return null;
            var uuid = src[marker.Length..].Split('#')[0];
            if (!_pages.Contains(uuid))
                return null;
            var bytes = Decode(uuid, out _);
            return bytes == null ? null : Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Bytes + mime của ảnh mà <c>src</c> trỏ tới: <c>data:image/…;base64,</c> hoặc uuid của bundle.</summary>
        public (byte[]? Bytes, string? Mime) ReadImage(string? src)
        {
            if (string.IsNullOrWhiteSpace(src))
                return (null, null);
            src = src.Trim();

            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = src.IndexOf(',');
                if (comma < 0)
                    return (null, null);
                var meta = src[5..comma];
                if (!meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    return (null, null);
                var mime = meta[..^";base64".Length].Split(';')[0].Trim().ToLowerInvariant();
                try { return (Convert.FromBase64String(src[(comma + 1)..]), mime); }
                catch (FormatException) { return (null, null); }
            }

            var uuid = src.Split('#')[0];
            var bytes = Decode(uuid, out var entryMime);
            return entryMime != null && entryMime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? (bytes, entryMime.ToLowerInvariant())
                : (null, null);
        }

        private byte[]? Decode(string uuid, out string? mime)
        {
            mime = null;
            if (!_entries.TryGetValue(uuid, out var entry) || entry.ValueKind != JsonValueKind.Object)
                return null;
            mime = entry.TryGetProperty("mime", out var m) ? m.GetString() : null;
            if (!entry.TryGetProperty("data", out var d) || d.GetString() is not { } data)
                return null;

            try
            {
                var raw = Convert.FromBase64String(data);
                var compressed = entry.TryGetProperty("compressed", out var c) && c.ValueKind == JsonValueKind.True;
                if (!compressed)
                    return raw;

                using var gzip = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > MaxDecodedAssetBytes)
                        return null; // gzip bomb / asset khổng lồ: bỏ.
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            foreach (var doc in _manifests)
                doc.Dispose();
        }
    }

    private static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;
        var sb = new StringBuilder(raw.Length);
        var space = false;
        foreach (var ch in raw)
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
        var text = sb.ToString();
        return text.Length > MaxCharsPerLine ? text[..MaxCharsPerLine] + "…" : text;
    }
}
