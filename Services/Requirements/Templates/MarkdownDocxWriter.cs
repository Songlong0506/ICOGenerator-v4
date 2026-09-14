using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
// Markdig.Extensions.Tables KHÔNG được import cả namespace: Table/TableRow/TableCell của nó trùng tên
// với ba lớp OpenXML mà file này dựng ra. Bí danh giữ cho mỗi tên chỉ có một nghĩa trong file.
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableColumnAlign = Markdig.Extensions.Tables.TableColumnAlign;
using MdTableColumnDefinition = Markdig.Extensions.Tables.TableColumnDefinition;

namespace ICOGenerator.Services.Requirements.Templates;

/// <summary>Thông tin trang bìa / header / footer của một tài liệu sinh từ Markdown.</summary>
/// <param name="Title">Loại tài liệu — in to trên bìa và ở header ("Product Brief", "AI Design Spec"…).</param>
/// <param name="ProjectName">Tên dự án trong hệ thống.</param>
/// <param name="VersionLabel">Nhãn phiên bản như lưu trong DB: <c>draft</c>, <c>V1</c>, <c>V2</c>…</param>
public record DocxDocumentMeta(
    string Title,
    string ProjectName,
    string VersionLabel,
    DateTime GeneratedAt,
    string Author = "BA Agent (ICOGenerator)");

/// <summary>
/// Dựng file .docx TRÌNH BÀY ĐƯỢC từ nội dung Markdown do LLM trả về (Product Brief, AI Design Spec,
/// User Stories).
///
/// <para>
/// Vì sao không đổ thẳng từng dòng vào từng paragraph như trước: nội dung LLM trả về là Markdown, nên
/// bản .docx cũ hiện nguyên ký tự <c>#</c>, <c>**</c>, <c>|</c> giữa văn bản, mọi dòng cùng một cỡ chữ,
/// không mục lục, không số trang. Đó là file người dùng GỬI CHO CẤP TRÊN duyệt — thứ họ nhận được phải
/// là tài liệu, không phải bản đổ thô. Lớp này dịch Markdown sang cấu trúc Word thật: heading có style
/// (nên Word tự dựng được mục lục và <see cref="DocxTemplateWriter.ExtractHtml"/> render đúng cấp),
/// danh sách có bullet/số thật, bảng Markdown thành bảng Word, đậm/nghiêng/mã/liên kết thành định dạng
/// run.
/// </para>
///
/// <para>
/// Hàm thuần file → file, không phụ thuộc DI: cùng lý do <c>ReviewPackageBuilder</c> là static.
/// </para>
/// </summary>
public static class MarkdownDocxWriter
{
    private const string AccentDark = "1F4E79";
    private const string Accent = "2E74B5";
    private const string Muted = "595959";
    private const string RuleColor = "BFCBD9";
    private const string BandFill = "F2F6FA";
    private const string CodeFill = "F4F5F7";
    private const string BodyFont = "Calibri";
    private const string HeadingFont = "Calibri Light";
    private const string MonoFont = "Consolas";

    // A4 dọc, lề 2cm (1134 twip) — cùng khổ với bộ template BRD/SRS/FSD nên in chung một tập không lệch.
    private const uint PageWidth = 11906;
    private const uint PageHeight = 16838;
    private const uint PageMarginTwips = 1134;

    /// <summary>
    /// Pipeline Markdig dùng cho mọi tài liệu. <c>UsePipeTables</c> cho bảng kiểu GFM (cú pháp mà prompt
    /// yêu cầu model dùng), <c>UseEmphasisExtras</c> cho <c>~~gạch ngang~~</c>. Dựng MỘT lần: pipeline là
    /// immutable và dựng lại cho mỗi tài liệu là tốn vô ích.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .Build();

    /// <summary>Sinh .docx từ Markdown. Trả về <paramref name="outputPath"/>.</summary>
    public static string Create(string outputPath, DocxDocumentMeta meta, string? markdown)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Ghi ra file tạm rồi mới move vào chỗ: hỏng giữa chừng không để lại .docx dở dang mà phía sau
        // vẫn coi là hợp lệ (cùng luật với DocxTemplateWriter.CreateFromTemplate).
        var tempPath = outputPath + ".tmp";

        try
        {
            using (var doc = WordprocessingDocument.Create(tempPath, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body());

                AddSettings(main);
                AddStyles(main);

                var numbering = AddNumbering(main);
                var body = main.Document.Body!;

                // Parse MỘT lần rồi render từ cây: trang bìa, mục lục và thân bài trước đây mỗi chỗ tự
                // quét lại dòng bằng regex riêng, nên "thế nào là một heading" được định nghĩa ba lần và
                // cả ba đều phải tự theo dõi trạng thái trong/ngoài khối ```.
                var document = Markdown.Parse(markdown ?? string.Empty, Pipeline);
                var subject = TakeLeadingTitle(document);
                var headingShift = MeasureHeadingShift(document);

                AppendCover(body, meta, subject);
                AppendTableOfContents(body, document, headingShift);

                var context = new RenderContext(main);
                AppendMarkdown(body, document, context, headingShift);
                numbering.Numbering!.Append(context.OrderedInstances);
                numbering.Numbering.Save();

                body.AppendChild(BuildSectionProperties(main, meta));

                main.Document.Save();
            }

            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return outputPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Dọn dẹp best-effort; lỗi gốc mới là lỗi đáng nổi lên.
        }
    }

    /// <summary>
    /// Lấy heading <c>#</c> mở đầu ra làm phụ đề trang bìa và GỠ nó khỏi cây. Không gỡ thì tên sản phẩm
    /// bị in hai lần — một lần trên bìa dưới dạng "Product Brief", một lần ngay dòng đầu thân bài.
    /// Chỉ nhận khi nó là khối ĐẦU TIÊN: một <c>#</c> nằm giữa tài liệu là một mục, không phải nhan đề.
    /// </summary>
    private static string? TakeLeadingTitle(MarkdownDocument document)
    {
        if (document.Count == 0 || document[0] is not HeadingBlock { Level: 1 } heading)
            return null;

        document.RemoveAt(0);

        return PlainText(heading.Inline);
    }

    /// <summary>
    /// Bậc cần nâng để mục cấp cao nhất của nội dung thành Heading 1. Prompt Product Brief đặt tên sản
    /// phẩm ở <c>#</c> và các mục ở <c>##</c>; tên sản phẩm đã lên trang bìa, nên nếu giữ nguyên bậc thì
    /// cả tài liệu không có Heading 1 nào — mục lục và khung điều hướng của Word thụt vào một cấp vô cớ.
    /// </summary>
    private static int MeasureHeadingShift(MarkdownDocument document)
    {
        // Descendants chỉ trả về heading THẬT: một dòng '# ...' nằm trong khối ``` là nội dung mã, và
        // trình phân tích đã tách nó thành FencedCodeBlock nên nó không lọt vào đây. Bản cũ phải tự đếm
        // hàng rào ở ba chỗ khác nhau để giả lập chuyện đó.
        var minimum = document.Descendants<HeadingBlock>()
            .Select(x => x.Level)
            .DefaultIfEmpty(0)
            .Min();

        return minimum == 0 ? 0 : minimum - 1;
    }

    /// <summary>
    /// Chữ THUẦN của một chuỗi inline — cho trang bìa và mục lục, nơi chỉ cần nội dung chứ không cần định
    /// dạng. Thay <c>StripInlineMarkers</c> cũ (xóa chuỗi "**"/"`" khỏi văn bản): cách đó cũng xóa luôn
    /// dấu sao trong một dòng cố tình nói về cú pháp Markdown, còn ở đây dấu nào là đánh dấu và dấu nào
    /// là nội dung đã được trình phân tích quyết định xong.
    /// </summary>
    private static string PlainText(ContainerInline? container)
    {
        if (container == null)
            return string.Empty;

        var text = new System.Text.StringBuilder();
        AppendPlainText(text, container);

        return text.ToString().Trim();
    }

    private static void AppendPlainText(System.Text.StringBuilder text, ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.AsSpan());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
                case ContainerInline nested:
                    AppendPlainText(text, nested);
                    break;
                case AutolinkInline autolink:
                    text.Append(autolink.Url);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- trang bìa

    private static void AppendCover(Body body, DocxDocumentMeta meta, string? subject)
    {
        // Tên dự án và tên sản phẩm ở đầu tài liệu thường là một: in cả hai thì trang bìa lặp chính nó.
        if (string.Equals(subject?.Trim(), meta.ProjectName.Trim(), StringComparison.OrdinalIgnoreCase))
            subject = null;

        body.AppendChild(Spacer(1400));
        body.AppendChild(AccentBar());

        body.AppendChild(StyledParagraph(
            meta.ProjectName.ToUpperInvariant(),
            new RunFormat { Bold = true, SizeHalfPoints = 20, Color = Accent, Spacing = 40 },
            spacingBefore: 240,
            spacingAfter: 120));

        body.AppendChild(StyledParagraph(
            meta.Title,
            new RunFormat { Bold = true, SizeHalfPoints = 72, Color = AccentDark, Font = HeadingFont },
            spacingBefore: 0,
            spacingAfter: 120));

        if (!string.IsNullOrWhiteSpace(subject))
            body.AppendChild(StyledParagraph(
                subject!,
                new RunFormat { SizeHalfPoints = 32, Color = Muted, Font = HeadingFont },
                spacingBefore: 0,
                spacingAfter: 240));

        body.AppendChild(AccentBar());
        body.AppendChild(Spacer(1200));
        body.AppendChild(BuildCoverTable(meta, subject));

        body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
    }

    private static Table BuildCoverTable(DocxDocumentMeta meta, string? subject)
    {
        var rows = new List<(string Label, string Value)>
        {
            ("Dự án", meta.ProjectName),
            ("Tài liệu", string.IsNullOrWhiteSpace(subject) ? meta.Title : $"{meta.Title} — {subject}"),
            ("Phiên bản", DescribeVersion(meta.VersionLabel)),
            ("Ngày lập", meta.GeneratedAt.ToString("dd/MM/yyyy")),
            ("Người soạn", meta.Author)
        };

        var table = new Table(
            new TableProperties(
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
                new TableBorders(
                    new TopBorder { Val = BorderValues.None },
                    new LeftBorder { Val = BorderValues.None },
                    new BottomBorder { Val = BorderValues.None },
                    new RightBorder { Val = BorderValues.None },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = RuleColor },
                    new InsideVerticalBorder { Val = BorderValues.None }),
                new TableCellMarginDefault(
                    new TopMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 0, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = 120, Type = TableWidthValues.Dxa })),
            new TableGrid(
                new GridColumn { Width = "2400" },
                new GridColumn { Width = "7000" }));

        foreach (var (label, value) in rows)
        {
            table.AppendChild(new TableRow(
                BuildCoverCell(label, new RunFormat { Bold = true, SizeHalfPoints = 18, Color = Muted, Spacing = 20 }, "2400"),
                BuildCoverCell(value, new RunFormat { SizeHalfPoints = 22, Color = "000000" }, "7000")));
        }

        return table;
    }

    private static TableCell BuildCoverCell(string text, RunFormat format, string width) =>
        new(
            new TableCellProperties(
                new TableCellWidth { Width = width, Type = TableWidthUnitValues.Dxa },
                new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }),
            StyledParagraph(text, format, spacingBefore: 80, spacingAfter: 80));

    private static string DescribeVersion(string? versionLabel) =>
        string.IsNullOrWhiteSpace(versionLabel) ? "—"
        : versionLabel.Equals("draft", StringComparison.OrdinalIgnoreCase) ? "Bản nháp (chưa duyệt)"
        : $"{versionLabel} (đã duyệt)";

    private static Paragraph Spacer(int height) =>
        new(new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = height.ToString(), Line = "240", LineRule = LineSpacingRuleValues.Auto }));

    private static Paragraph AccentBar() =>
        new(
            new ParagraphProperties(
                new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 24, Color = AccentDark }),
                new SpacingBetweenLines { Before = "0", After = "0", Line = "120", LineRule = LineSpacingRuleValues.Exact }));

    // ---------------------------------------------------------------- mục lục

    /// <summary>
    /// Mục lục dựng bằng field <c>TOC</c> THẬT (Word tự đánh số trang khi mở, xem
    /// <see cref="AddSettings"/>) nhưng kết quả field được điền sẵn danh sách heading, để bản mở bằng
    /// công cụ không cập nhật field (Google Docs, LibreOffice, khung xem trước) vẫn thấy nội dung chứ
    /// không phải một trang trắng.
    /// </summary>
    private static void AppendTableOfContents(Body body, MarkdownDocument document, int headingShift)
    {
        var entries = document.Descendants<HeadingBlock>()
            .Select(x => (Level: Math.Max(1, x.Level - headingShift), Text: PlainText(x.Inline)))
            .Where(x => x.Level <= 2)
            .ToList();

        if (entries.Count < 3)
            return;

        body.AppendChild(StyledParagraph(
            "Mục lục",
            new RunFormat { Bold = true, SizeHalfPoints = 32, Color = AccentDark, Font = HeadingFont },
            spacingBefore: 0,
            spacingAfter: 200));

        for (var i = 0; i < entries.Count; i++)
        {
            var (level, text) = entries[i];

            var paragraph = new Paragraph(new ParagraphProperties(
                new ParagraphStyleId { Val = level == 1 ? "TOC1" : "TOC2" },
                new Tabs(new TabStop { Val = TabStopValues.Right, Leader = TabStopLeaderCharValues.Dot, Position = 9060 })));

            if (i == 0)
            {
                paragraph.AppendChild(new Run(new FieldChar { FieldCharType = FieldCharValues.Begin, Dirty = true }));
                paragraph.AppendChild(new Run(new FieldCode(" TOC \\o \"1-2\" \\h \\z \\u ") { Space = SpaceProcessingModeValues.Preserve }));
                paragraph.AppendChild(new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }));
            }

            paragraph.AppendChild(BuildRun(text, new RunFormat()));

            if (i == entries.Count - 1)
                paragraph.AppendChild(new Run(new FieldChar { FieldCharType = FieldCharValues.End }));

            body.AppendChild(paragraph);
        }

        body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
    }

    // ---------------------------------------------------------------- thân bài

    private sealed class RenderContext
    {
        public RenderContext(MainDocumentPart main) => Main = main;

        public MainDocumentPart Main { get; }

        /// <summary>Instance numbering của các danh sách đánh số — mỗi danh sách một instance để đếm lại từ 1.</summary>
        public List<NumberingInstance> OrderedInstances { get; } = new();

        /// <summary>
        /// Một instance numbering cho MỖI danh sách đánh số, khóa theo chính node danh sách đó.
        /// <para>
        /// Bản cũ khóa theo BẬC THỤT LỀ và phải tự dựng lại cấu trúc lồng nhau từ số dấu cách đầu dòng
        /// (<c>IndentStack</c>/<c>ResolveLevel</c>), vì regex quét dòng không biết danh sách nào lồng
        /// trong danh sách nào. Trình phân tích trả về cây có sẵn quan hệ đó, nên khóa theo node vừa gọn
        /// hơn vừa đúng hơn: hai danh sách anh em ở CÙNG một bậc là hai node khác nhau nên mỗi cái đếm
        /// lại từ 1 — bản cũ dùng chung numId cho tới khi gặp dòng trống.
        /// </para>
        /// </summary>
        private readonly Dictionary<ListBlock, int> _orderedNumIds = new();

        private int _nextNumberingId = 100;

        public int OrderedNumIdFor(ListBlock list)
        {
            if (_orderedNumIds.TryGetValue(list, out var existing))
                return existing;

            var numId = _nextNumberingId++;

            OrderedInstances.Add(BuildOrderedInstance(numId));
            _orderedNumIds[list] = numId;

            return numId;
        }
    }

    /// <summary>
    /// Đổ các khối của cây Markdown vào thân tài liệu Word.
    /// <para>
    /// Đây là chỗ thay thế vòng <c>while</c> quét từng dòng của bản cũ. Vòng đó phải tự nhận diện bảy
    /// dạng khối bằng bảy regex, tự gom dòng cho đoạn văn bằng cách liệt kê "không phải heading, không
    /// phải bullet, không phải..." (mỗi dạng khối thêm vào là thêm một vế phủ định ở đó), tự theo dõi
    /// hàng rào <c>```</c>, và tự đoán cấu trúc lồng nhau của danh sách từ số dấu cách đầu dòng. Trình
    /// phân tích đã làm hết những việc đó, nên chỗ này chỉ còn là một bảng phân nhánh theo kiểu khối.
    /// </para>
    /// </summary>
    private static void AppendMarkdown(Body body, MarkdownDocument document, RenderContext context, int headingShift)
    {
        foreach (var block in document)
            AppendBlock(body, block, context, headingShift, listLevel: 0);
    }

    private static void AppendBlock(OpenXmlElement target, Block block, RenderContext context, int headingShift, int listLevel)
    {
        switch (block)
        {
            case HeadingBlock heading:
                target.AppendChild(BuildHeading(heading.Level - headingShift, heading.Inline, context));
                break;

            case ParagraphBlock paragraph:
                target.AppendChild(BuildBodyParagraph(paragraph.Inline, context));
                break;

            case ListBlock list:
                AppendList(target, list, context, headingShift, listLevel);
                break;

            case QuoteBlock quote:
                AppendQuote(target, quote, context);
                break;

            case CodeBlock code:
                target.AppendChild(BuildCodeBlock(CodeLines(code)));
                break;

            case ThematicBreakBlock:
                target.AppendChild(AccentBar());
                break;

            case MdTable table:
                target.AppendChild(BuildTable(table, context));
                target.AppendChild(Spacer(120));
                break;

            // HTML thô model chèn vào: in nguyên văn thay vì nuốt mất. Khối lạ nào chưa có nhánh riêng
            // cũng rơi về đây chứ không biến mất khỏi tài liệu.
            case LeafBlock leaf when leaf.Lines.Count > 0:
                target.AppendChild(BuildBodyParagraph(leaf.Lines.ToString(), context));
                break;
        }
    }

    /// <summary>
    /// Một danh sách và mọi danh sách con của nó. <paramref name="level"/> là bậc lồng THẬT lấy từ cây,
    /// không còn suy từ số dấu cách đầu dòng; Word chỉ định nghĩa ba bậc nên bậc sâu hơn bị kẹp lại.
    /// </summary>
    private static void AppendList(OpenXmlElement target, ListBlock list, RenderContext context, int headingShift, int level)
    {
        var numId = list.IsOrdered ? context.OrderedNumIdFor(list) : BulletNumId;
        var itemLevel = Math.Min(level, 2);

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var first = true;

            foreach (var child in item)
            {
                // Đoạn ĐẦU của mục là dòng mang bullet/số. Mọi thứ sau nó (đoạn tiếp theo, khối mã, bảng,
                // danh sách con) là nội dung THUỘC mục — bản cũ không có khái niệm này nên một khối mã
                // thụt lề trong một mục sẽ cắt đứt danh sách.
                if (first && child is ParagraphBlock paragraph)
                {
                    target.AppendChild(BuildListItem(paragraph.Inline, itemLevel, numId, context));
                    first = false;
                    continue;
                }

                first = false;

                if (child is ListBlock nested)
                    AppendList(target, nested, context, headingShift, level + 1);
                else
                    AppendBlock(target, child, context, headingShift, level + 1);
            }
        }
    }

    private static void AppendQuote(OpenXmlElement target, QuoteBlock quote, RenderContext context)
    {
        foreach (var child in quote)
        {
            if (child is ParagraphBlock paragraph)
                target.AppendChild(BuildQuote(paragraph.Inline, context));
            else if (child is LeafBlock leaf && leaf.Lines.Count > 0)
                target.AppendChild(BuildQuote(leaf.Lines.ToString(), context));
        }
    }

    private static List<string> CodeLines(CodeBlock code)
    {
        var lines = new List<string>(code.Lines.Count);

        for (var i = 0; i < code.Lines.Count; i++)
            lines.Add(code.Lines.Lines[i].Slice.ToString());

        return lines;
    }


    private static Paragraph BuildHeading(int level, ContainerInline? inline, RenderContext context)
    {
        var styleId = "Heading" + Math.Clamp(level, 1, 4);

        var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = styleId }));
        paragraph.Append(RenderInline(inline, new RunFormat(), context));

        return paragraph;
    }

    private static Paragraph BuildBodyParagraph(ContainerInline? inline, RenderContext context)
    {
        var paragraph = new Paragraph();
        paragraph.Append(RenderInline(inline, new RunFormat(), context));

        return paragraph;
    }

    /// <summary>Đoạn văn từ chữ THUẦN (khối HTML thô, khối lạ) — không có inline để duyệt.</summary>
    private static Paragraph BuildBodyParagraph(string text, RenderContext context)
    {
        var paragraph = new Paragraph();
        paragraph.AppendChild(BuildRun(text.TrimEnd(), new RunFormat()));

        return paragraph;
    }

    private static Paragraph BuildListItem(ContainerInline? inline, int level, int numId, RenderContext context)
    {
        var paragraph = new Paragraph(new ParagraphProperties(
            new ParagraphStyleId { Val = "ListParagraph" },
            new NumberingProperties(
                new NumberingLevelReference { Val = level },
                new NumberingId { Val = numId })));

        paragraph.Append(RenderInline(inline, new RunFormat(), context));

        return paragraph;
    }

    private static Paragraph BuildQuote(ContainerInline? inline, RenderContext context)
    {
        var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Quote" }));
        paragraph.Append(RenderInline(inline, new RunFormat { Italic = true, Color = Muted }, context));

        return paragraph;
    }

    private static Paragraph BuildQuote(string text, RenderContext context)
    {
        var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Quote" }));
        paragraph.AppendChild(BuildRun(text.TrimEnd(), new RunFormat { Italic = true, Color = Muted }));

        return paragraph;
    }

    private static Paragraph BuildCodeBlock(IReadOnlyList<string> codeLines)
    {
        var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "CodeBlock" }));

        for (var i = 0; i < codeLines.Count; i++)
        {
            if (i > 0)
                paragraph.AppendChild(new Run(new Break()));

            paragraph.AppendChild(BuildRun(codeLines[i], new RunFormat { Font = MonoFont, SizeHalfPoints = 18 }));
        }

        return paragraph;
    }

    private static Table BuildTable(MdTable source, RenderContext context)
    {
        var rows = source.OfType<MdTableRow>().ToList();
        var alignments = source.ColumnDefinitions.Select(AlignmentOf).ToList();
        var columnCount = rows.Count == 0 ? 1 : Math.Max(1, rows.Max(r => r.OfType<MdTableCell>().Count()));

        var table = new Table(
            new TableProperties(
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4, Color = RuleColor },
                    new LeftBorder { Val = BorderValues.Single, Size = 4, Color = RuleColor },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = RuleColor },
                    new RightBorder { Val = BorderValues.Single, Size = 4, Color = RuleColor },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = RuleColor },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = RuleColor }),
                new TableCellMarginDefault(
                    new TopMargin { Width = "60", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 108, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "60", Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = 108, Type = TableWidthValues.Dxa }),
                new TableLook { Val = "04A0" }));

        var grid = new TableGrid();

        for (var i = 0; i < columnCount; i++)
            grid.AppendChild(new GridColumn { Width = (9638 / columnCount).ToString() });

        table.AppendChild(grid);

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            // Trình phân tích tự đánh dấu hàng tiêu đề; bản cũ mặc định "hàng đầu tiên", nên một bảng
            // GFM không có hàng tiêu đề bị in hàng dữ liệu đầu tiên dưới nền xanh đậm chữ trắng.
            var isHeader = rows[rowIndex].IsHeader;

            var row = new TableRow();

            if (isHeader)
                row.AppendChild(new TableRowProperties(new TableHeader()));

            var sourceCells = rows[rowIndex].OfType<MdTableCell>().ToList();

            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var cell = columnIndex < sourceCells.Count ? sourceCells[columnIndex] : null;

                var alignment = columnIndex < alignments.Count ? alignments[columnIndex] : JustificationValues.Left;

                var cellProperties = new TableCellProperties(
                    new TableCellWidth { Width = (9638 / columnCount).ToString(), Type = TableWidthUnitValues.Dxa });

                if (isHeader)
                    cellProperties.AppendChild(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = AccentDark });
                else if (rowIndex % 2 == 0)
                    cellProperties.AppendChild(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = BandFill });

                cellProperties.AppendChild(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });

                var paragraph = new Paragraph(new ParagraphProperties(
                    new ParagraphStyleId { Val = "TableText" },
                    new Justification { Val = alignment }));

                var format = isHeader
                    ? new RunFormat { Bold = true, Color = "FFFFFF" }
                    : new RunFormat();

                paragraph.Append(RenderInline(CellInline(cell), format, context));

                row.AppendChild(new TableCell(cellProperties, paragraph));
            }

            table.AppendChild(row);
        }

        return table;
    }

    /// <summary>
    /// Nội dung inline của một ô. Ô GFM chứa nguyên một khối (thường là một đoạn văn), nên phải đi xuống
    /// một cấp mới tới chỗ có định dạng — nhờ vậy <c>**đậm**</c> trong ô bảng mới thành chữ đậm thật.
    /// </summary>
    private static ContainerInline? CellInline(MdTableCell? cell) =>
        cell?.OfType<LeafBlock>().FirstOrDefault()?.Inline;

    private static JustificationValues AlignmentOf(MdTableColumnDefinition column) => column.Alignment switch
    {
        MdTableColumnAlign.Center => JustificationValues.Center,
        MdTableColumnAlign.Right => JustificationValues.Right,
        _ => JustificationValues.Left
    };

    // ---------------------------------------------------------------- định dạng trong dòng

    private sealed record RunFormat
    {
        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public bool Strike { get; init; }
        public bool Code { get; init; }
        public bool Hyperlink { get; init; }
        public string? Color { get; init; }
        public string? Font { get; init; }
        public int? SizeHalfPoints { get; init; }
        public int? Spacing { get; init; }
    }

    /// <summary>
    /// Đổ một chuỗi inline (đậm/nghiêng/mã/gạch ngang/liên kết) thành các run Word. Không làm bước này
    /// thì mọi ký tự đánh dấu nằm nguyên trên trang giấy gửi cấp trên.
    /// <para>
    /// Thay bộ quét ký tự tự viết (<c>ReadMarker</c>/<c>FindClosingMarker</c>/<c>TryReadLink</c> và một
    /// vòng <c>for</c> tự xử lý dấu thoát <c>\</c>). Bộ đó phải tự đoán ranh giới nhấn mạnh — vì sao
    /// <c>snake_case</c> không phải chữ nghiêng là một hàm riêng ở đó — trong khi đây đúng là phần rắc
    /// rối nhất của đặc tả Markdown và trình phân tích đã cài đủ.
    /// </para>
    /// </summary>
    private static List<OpenXmlElement> RenderInline(ContainerInline? container, RunFormat format, RenderContext context)
    {
        var elements = new List<OpenXmlElement>();

        if (container != null)
            AppendInline(elements, container, format, context);

        // Word cần ít nhất một run trong paragraph để giữ được định dạng của nó.
        if (elements.Count == 0)
            elements.Add(BuildRun("", format));

        return elements;
    }

    private static void AppendInline(List<OpenXmlElement> elements, ContainerInline container, RunFormat format, RenderContext context)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    elements.Add(BuildRun(literal.Content.ToString(), format));
                    break;

                case CodeInline code:
                    elements.Add(BuildRun(code.Content, format with { Code = true }));
                    break;

                case EmphasisInline emphasis:
                    AppendInline(elements, emphasis, EmphasisFormat(emphasis, format), context);
                    break;

                case LinkInline { IsImage: false } link:
                    AppendHyperlink(elements, link, format, context);
                    break;

                // Ảnh: .docx sinh ở đây không nhúng ảnh, nên in chữ thay thế để người đọc biết chỗ đó có
                // hình chứ không phải một khoảng trống không giải thích được.
                case LinkInline { IsImage: true } image:
                    var caption = PlainText(image);
                    if (!string.IsNullOrWhiteSpace(caption))
                        elements.Add(BuildRun($"[hình: {caption}]", format with { Italic = true, Color = Muted }));
                    break;

                case AutolinkInline autolink:
                    AppendHyperlinkTo(elements, autolink.Url, autolink.Url, format, context);
                    break;

                // Xuống dòng mềm trong một đoạn văn: Markdown gộp thành một khoảng trắng. Đúng hành vi cũ
                // (bản cũ gom các dòng của đoạn rồi join bằng " ").
                case LineBreakInline { IsHard: false }:
                    elements.Add(BuildRun(" ", format));
                    break;

                case LineBreakInline { IsHard: true }:
                    elements.Add(new Run(new Break()));
                    break;

                case HtmlInline html:
                    elements.Add(BuildRun(html.Tag, format));
                    break;

                case ContainerInline nested:
                    AppendInline(elements, nested, format, context);
                    break;

                case LeafInline leaf:
                    elements.Add(BuildRun(leaf.ToString() ?? string.Empty, format));
                    break;
            }
        }
    }

    private static RunFormat EmphasisFormat(EmphasisInline emphasis, RunFormat format) =>
        emphasis.DelimiterChar switch
        {
            '~' => format with { Strike = true },
            _ => emphasis.DelimiterCount >= 2 ? format with { Bold = true } : format with { Italic = true }
        };


    private static void AppendHyperlink(List<OpenXmlElement> elements, LinkInline link, RunFormat format, RenderContext context)
    {
        var linkFormat = format with { Hyperlink = true };

        // URL tương đối / không parse được: vẫn in nhãn (có định dạng liên kết) chứ không bỏ đi — nhưng
        // không tạo relationship, vì một relationship hỏng làm Word báo tài liệu lỗi.
        if (!Uri.TryCreate(link.Url ?? string.Empty, UriKind.Absolute, out var uri))
        {
            AppendInline(elements, link, linkFormat, context);
            return;
        }

        var relationship = context.Main.AddHyperlinkRelationship(uri, true);
        var hyperlink = new Hyperlink { Id = relationship.Id };

        var inner = new List<OpenXmlElement>();
        AppendInline(inner, link, linkFormat, context);

        if (inner.Count == 0)
            inner.Add(BuildRun(link.Url!, linkFormat));

        hyperlink.Append(inner);
        elements.Add(hyperlink);
    }

    /// <summary>Liên kết từ chữ thuần (autolink <c>&lt;https://…&gt;</c>) — không có inline con để duyệt.</summary>
    private static void AppendHyperlinkTo(List<OpenXmlElement> elements, string label, string url, RunFormat format, RenderContext context)
    {
        var linkFormat = format with { Hyperlink = true };

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            elements.Add(BuildRun(label, linkFormat));
            return;
        }

        var relationship = context.Main.AddHyperlinkRelationship(uri, true);
        elements.Add(new Hyperlink(BuildRun(label, linkFormat)) { Id = relationship.Id });
    }

    private static Run BuildRun(string text, RunFormat format)
    {
        var properties = new RunProperties();

        if (format.Code)
            properties.AppendChild(new RunFonts { Ascii = MonoFont, HighAnsi = MonoFont });
        else if (!string.IsNullOrEmpty(format.Font))
            properties.AppendChild(new RunFonts { Ascii = format.Font, HighAnsi = format.Font });

        if (format.Bold)
            properties.AppendChild(new Bold());

        if (format.Italic)
            properties.AppendChild(new Italic());

        if (format.Strike)
            properties.AppendChild(new Strike());

        var color = format.Hyperlink ? Accent : format.Code ? "9C2D41" : format.Color;

        if (!string.IsNullOrEmpty(color))
            properties.AppendChild(new Color { Val = color });

        if (format.Spacing.HasValue)
            properties.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Spacing { Val = format.Spacing.Value });

        var size = format.SizeHalfPoints ?? (format.Code ? 19 : (int?)null);

        if (size.HasValue)
        {
            properties.AppendChild(new FontSize { Val = size.Value.ToString() });
            properties.AppendChild(new FontSizeComplexScript { Val = size.Value.ToString() });
        }

        if (format.Hyperlink)
            properties.AppendChild(new Underline { Val = UnderlineValues.Single });

        if (format.Code)
            properties.AppendChild(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = CodeFill });

        var run = new Run();

        if (properties.HasChildren)
            run.AppendChild(properties);

        run.AppendChild(new Text(DocxTemplateWriter.SanitizeXmlText(text)) { Space = SpaceProcessingModeValues.Preserve });

        return run;
    }

    private static Paragraph StyledParagraph(string text, RunFormat format, int spacingBefore, int spacingAfter)
    {
        var paragraph = new Paragraph(new ParagraphProperties(
            new SpacingBetweenLines
            {
                Before = spacingBefore.ToString(),
                After = spacingAfter.ToString(),
                Line = "240",
                LineRule = LineSpacingRuleValues.Auto
            }));

        paragraph.AppendChild(BuildRun(text, format));

        return paragraph;
    }

    // ---------------------------------------------------------------- header / footer / section

    private static SectionProperties BuildSectionProperties(MainDocumentPart main, DocxDocumentMeta meta)
    {
        var header = main.AddNewPart<HeaderPart>();
        header.Header = BuildHeader(meta);
        header.Header.Save();

        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = BuildFooter(meta);
        footer.Footer.Save();

        // Trang bìa không đeo header/footer: một trang bìa có "Trang 1/9" ở chân là dấu hiệu rõ nhất của
        // file xuất tự động.
        var firstHeader = main.AddNewPart<HeaderPart>();
        firstHeader.Header = new Header(new Paragraph());
        firstHeader.Header.Save();

        var firstFooter = main.AddNewPart<FooterPart>();
        firstFooter.Footer = new Footer(new Paragraph());
        firstFooter.Footer.Save();

        return new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
            new HeaderReference { Type = HeaderFooterValues.First, Id = main.GetIdOfPart(firstHeader) },
            new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) },
            new FooterReference { Type = HeaderFooterValues.First, Id = main.GetIdOfPart(firstFooter) },
            new PageSize { Width = PageWidth, Height = PageHeight },
            new PageMargin
            {
                Top = (int)PageMarginTwips,
                Right = PageMarginTwips,
                Bottom = (int)PageMarginTwips,
                Left = PageMarginTwips,
                Header = 680,
                Footer = 680,
                Gutter = 0
            },
            new Columns { Space = "708" },
            new TitlePage(),
            new DocGrid { LinePitch = 360 });
    }

    private static Header BuildHeader(DocxDocumentMeta meta)
    {
        var paragraph = new Paragraph(new ParagraphProperties(
            new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 4, Color = RuleColor }),
            new Tabs(new TabStop { Val = TabStopValues.Right, Position = 9060 }),
            new SpacingBetweenLines { Before = "0", After = "60", Line = "240", LineRule = LineSpacingRuleValues.Auto }));

        var format = new RunFormat { SizeHalfPoints = 16, Color = Muted };

        paragraph.AppendChild(BuildRun(meta.Title, format with { Bold = true }));
        paragraph.AppendChild(new Run(new TabChar()));
        paragraph.AppendChild(BuildRun($"{meta.ProjectName} · {DescribeVersion(meta.VersionLabel)}", format));

        return new Header(paragraph);
    }

    private static Footer BuildFooter(DocxDocumentMeta meta)
    {
        var paragraph = new Paragraph(new ParagraphProperties(
            new Tabs(new TabStop { Val = TabStopValues.Right, Position = 9060 }),
            new SpacingBetweenLines { Before = "60", After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }));

        var format = new RunFormat { SizeHalfPoints = 16, Color = Muted };

        paragraph.AppendChild(BuildRun($"{meta.Title} · lập ngày {meta.GeneratedAt:dd/MM/yyyy}", format));
        paragraph.AppendChild(new Run(new TabChar()));
        paragraph.AppendChild(BuildRun("Trang ", format));
        paragraph.Append(BuildField("PAGE", format));
        paragraph.AppendChild(BuildRun(" / ", format));
        paragraph.Append(BuildField("NUMPAGES", format));

        return new Footer(paragraph);
    }

    private static IEnumerable<OpenXmlElement> BuildField(string instruction, RunFormat format)
    {
        yield return new Run(new FieldChar { FieldCharType = FieldCharValues.Begin });
        yield return new Run(new FieldCode($" {instruction} ") { Space = SpaceProcessingModeValues.Preserve });
        yield return new Run(new FieldChar { FieldCharType = FieldCharValues.Separate });
        yield return BuildRun("1", format);
        yield return new Run(new FieldChar { FieldCharType = FieldCharValues.End });
    }

    // ---------------------------------------------------------------- styles / numbering / settings

    private static void AddSettings(MainDocumentPart main)
    {
        var part = main.AddNewPart<DocumentSettingsPart>();

        // Bảo Word cập nhật field khi mở: mục lục có số trang thật thay vì bản tĩnh dựng sẵn.
        part.Settings = new DocumentFormat.OpenXml.Wordprocessing.Settings(new UpdateFieldsOnOpen { Val = true });
        part.Settings.Save();
    }

    private const int BulletNumId = 1;
    private const int BulletAbstractId = 0;
    private const int OrderedAbstractId = 1;

    private static NumberingDefinitionsPart AddNumbering(MainDocumentPart main)
    {
        var part = main.AddNewPart<NumberingDefinitionsPart>();

        var bullet = new AbstractNum(
            BuildBulletLevel(0, "", 360),
            BuildBulletLevel(1, "o", 720),
            BuildBulletLevel(2, "", 1080))
        { AbstractNumberId = BulletAbstractId };

        var ordered = new AbstractNum(
            BuildOrderedLevel(0, NumberFormatValues.Decimal, "%1.", 360),
            BuildOrderedLevel(1, NumberFormatValues.LowerLetter, "%2.", 720),
            BuildOrderedLevel(2, NumberFormatValues.LowerRoman, "%3.", 1080))
        { AbstractNumberId = OrderedAbstractId };

        part.Numbering = new Numbering(
            bullet,
            ordered,
            new NumberingInstance(new AbstractNumId { Val = BulletAbstractId }) { NumberID = BulletNumId });

        return part;
    }

    private static Level BuildBulletLevel(int index, string text, int indent) =>
        new(
            new StartNumberingValue { Val = 1 },
            new NumberingFormat { Val = NumberFormatValues.Bullet },
            new LevelText { Val = text },
            new LevelJustification { Val = LevelJustificationValues.Left },
            new PreviousParagraphProperties(new Indentation { Left = indent.ToString(), Hanging = "360" }),
            new NumberingSymbolRunProperties(new RunFonts { Ascii = "Symbol", HighAnsi = "Symbol", Hint = FontTypeHintValues.Default }))
        { LevelIndex = index };

    private static Level BuildOrderedLevel(int index, NumberFormatValues format, string text, int indent) =>
        new(
            new StartNumberingValue { Val = 1 },
            new NumberingFormat { Val = format },
            new LevelText { Val = text },
            new LevelJustification { Val = LevelJustificationValues.Left },
            new PreviousParagraphProperties(new Indentation { Left = indent.ToString(), Hanging = "360" }))
        { LevelIndex = index };

    /// <summary>Mỗi danh sách đánh số một instance có <c>startOverride</c>, nếu không danh sách thứ hai đếm tiếp danh sách thứ nhất.</summary>
    private static NumberingInstance BuildOrderedInstance(int numId)
    {
        var instance = new NumberingInstance(new AbstractNumId { Val = OrderedAbstractId }) { NumberID = numId };

        for (var level = 0; level < 3; level++)
            instance.AppendChild(new LevelOverride(new StartOverrideNumberingValue { Val = 1 }) { LevelIndex = level });

        return instance;
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();

        var styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = BodyFont, HighAnsi = BodyFont, ComplexScript = BodyFont },
                    new FontSize { Val = "22" },
                    new FontSizeComplexScript { Val = "22" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "140", Line = "276", LineRule = LineSpacingRuleValues.Auto }))));

        styles.AppendChild(BuildStyle(
            "Normal", "Normal", isDefault: true,
            paragraph: new ParagraphProperties(new SpacingBetweenLines { After = "140", Line = "276", LineRule = LineSpacingRuleValues.Auto }),
            run: new StyleRunProperties(new RunFonts { Ascii = BodyFont, HighAnsi = BodyFont }, new FontSize { Val = "22" })));

        styles.AppendChild(BuildStyle(
            "Title", "Title", basedOn: "Normal", next: "Normal",
            paragraph: new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "120" }),
            run: new StyleRunProperties(
                new RunFonts { Ascii = HeadingFont, HighAnsi = HeadingFont },
                new Bold(),
                new Color { Val = AccentDark },
                new FontSize { Val = "56" })));

        styles.AppendChild(BuildStyle(
            "Heading1", "heading 1", basedOn: "Normal", next: "Normal",
            paragraph: new ParagraphProperties(
                new KeepNext(),
                new KeepLines(),
                new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 6, Color = RuleColor, Space = 6 }),
                new SpacingBetweenLines { Before = "400", After = "160", Line = "264", LineRule = LineSpacingRuleValues.Auto },
                new OutlineLevel { Val = 0 }),
            run: new StyleRunProperties(
                new RunFonts { Ascii = HeadingFont, HighAnsi = HeadingFont },
                new Bold(),
                new Color { Val = AccentDark },
                new FontSize { Val = "34" })));

        styles.AppendChild(BuildStyle(
            "Heading2", "heading 2", basedOn: "Normal", next: "Normal",
            paragraph: new ParagraphProperties(
                new KeepNext(),
                new KeepLines(),
                new SpacingBetweenLines { Before = "300", After = "120", Line = "264", LineRule = LineSpacingRuleValues.Auto },
                new OutlineLevel { Val = 1 }),
            run: new StyleRunProperties(
                new RunFonts { Ascii = HeadingFont, HighAnsi = HeadingFont },
                new Bold(),
                new Color { Val = Accent },
                new FontSize { Val = "28" })));

        styles.AppendChild(BuildStyle(
            "Heading3", "heading 3", basedOn: "Normal", next: "Normal",
            paragraph: new ParagraphProperties(
                new KeepNext(),
                new KeepLines(),
                new SpacingBetweenLines { Before = "240", After = "100" },
                new OutlineLevel { Val = 2 }),
            run: new StyleRunProperties(
                new Bold(),
                new Color { Val = "44546A" },
                new FontSize { Val = "24" })));

        styles.AppendChild(BuildStyle(
            "Heading4", "heading 4", basedOn: "Normal", next: "Normal",
            paragraph: new ParagraphProperties(
                new KeepNext(),
                new SpacingBetweenLines { Before = "200", After = "80" },
                new OutlineLevel { Val = 3 }),
            run: new StyleRunProperties(
                new Bold(),
                new Italic(),
                new Color { Val = "44546A" },
                new FontSize { Val = "22" })));

        styles.AppendChild(BuildStyle(
            "ListParagraph", "List Paragraph", basedOn: "Normal", next: "Normal",
            paragraph: new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "80", Line = "276", LineRule = LineSpacingRuleValues.Auto }),
            run: null));

        styles.AppendChild(BuildStyle(
            "Quote", "Quote", basedOn: "Normal", next: "Normal",
            paragraph: new ParagraphProperties(
                new ParagraphBorders(new LeftBorder { Val = BorderValues.Single, Size = 18, Color = Accent, Space = 8 }),
                new SpacingBetweenLines { Before = "160", After = "160" },
                new Indentation { Left = "340" }),
            run: new StyleRunProperties(new Italic(), new Color { Val = Muted })));

        styles.AppendChild(BuildStyle(
            "CodeBlock", "Code Block", basedOn: "Normal", next: "Normal",
            paragraph: new ParagraphProperties(
                new ParagraphBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4, Color = RuleColor, Space = 4 },
                    new LeftBorder { Val = BorderValues.Single, Size = 4, Color = RuleColor, Space = 4 },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = RuleColor, Space = 4 },
                    new RightBorder { Val = BorderValues.Single, Size = 4, Color = RuleColor, Space = 4 }),
                new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = CodeFill },
                new SpacingBetweenLines { Before = "120", After = "160", Line = "240", LineRule = LineSpacingRuleValues.Auto },
                new Indentation { Left = "120", Right = "120" }),
            run: new StyleRunProperties(new RunFonts { Ascii = MonoFont, HighAnsi = MonoFont }, new FontSize { Val = "18" })));

        styles.AppendChild(BuildStyle(
            "TableText", "Table Text", basedOn: "Normal", next: "Normal",
            paragraph: new ParagraphProperties(new SpacingBetweenLines { Before = "40", After = "40", Line = "240", LineRule = LineSpacingRuleValues.Auto }),
            run: new StyleRunProperties(new FontSize { Val = "20" })));

        styles.AppendChild(BuildStyle(
            "TOC1", "toc 1", basedOn: "Normal", next: "Normal",
            paragraph: new ParagraphProperties(new SpacingBetweenLines { Before = "120", After = "40" }),
            run: new StyleRunProperties(new Bold(), new Color { Val = AccentDark })));

        styles.AppendChild(BuildStyle(
            "TOC2", "toc 2", basedOn: "Normal", next: "Normal",
            paragraph: new ParagraphProperties(
                new SpacingBetweenLines { Before = "0", After = "40" },
                new Indentation { Left = "340" }),
            run: new StyleRunProperties(new Color { Val = Muted })));

        part.Styles = styles;
        part.Styles.Save();
    }

    private static Style BuildStyle(
        string styleId,
        string name,
        ParagraphProperties? paragraph = null,
        StyleRunProperties? run = null,
        string? basedOn = null,
        string? next = null,
        bool isDefault = false)
    {
        var style = new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = styleId,
            Default = isDefault ? OnOffValue.FromBoolean(true) : null
        };

        style.AppendChild(new StyleName { Val = name });

        if (!string.IsNullOrEmpty(basedOn))
            style.AppendChild(new BasedOn { Val = basedOn });

        if (!string.IsNullOrEmpty(next))
            style.AppendChild(new NextParagraphStyle { Val = next });

        style.AppendChild(new PrimaryStyle());

        if (paragraph != null)
            style.AppendChild(new StyleParagraphProperties(paragraph.ChildElements.Select(x => x.CloneNode(true))));

        if (run != null)
            style.AppendChild(run);

        return style;
    }

}
