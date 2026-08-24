using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

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

    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex OrderedRegex = new(@"^(\s*)(\d{1,3})[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex FenceRegex = new(@"^\s*(```|~~~)", RegexOptions.Compiled);
    private static readonly Regex HorizontalRuleRegex = new(@"^\s*([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex TableSeparatorRegex = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex QuoteRegex = new(@"^\s*>\s?(.*)$", RegexOptions.Compiled);

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

                var lines = SplitLines(markdown);
                var subject = TakeLeadingTitle(lines);
                var headingShift = MeasureHeadingShift(lines);

                AppendCover(body, meta, subject);
                AppendTableOfContents(body, lines, headingShift);

                var context = new RenderContext(main);
                AppendMarkdown(body, lines, context, headingShift);
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

    private static List<string> SplitLines(string? markdown) =>
        (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    /// <summary>
    /// Lấy dòng <c>#</c> mở đầu ra làm phụ đề trang bìa. Không lấy thì tên sản phẩm bị in hai lần —
    /// một lần trên bìa dưới dạng "Product Brief", một lần ngay dòng đầu thân bài.
    /// </summary>
    private static string? TakeLeadingTitle(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var match = HeadingRegex.Match(lines[i]);

            if (match.Success && match.Groups[1].Value.Length == 1)
            {
                var title = match.Groups[2].Value.Trim();
                lines.RemoveAt(i);
                return StripInlineMarkers(title);
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// Bậc cần nâng để mục cấp cao nhất của nội dung thành Heading 1. Prompt Product Brief đặt tên sản
    /// phẩm ở <c>#</c> và các mục ở <c>##</c>; tên sản phẩm đã lên trang bìa, nên nếu giữ nguyên bậc thì
    /// cả tài liệu không có Heading 1 nào — mục lục và khung điều hướng của Word thụt vào một cấp vô cớ.
    /// </summary>
    private static int MeasureHeadingShift(IReadOnlyList<string> lines)
    {
        var minimum = int.MaxValue;

        foreach (var line in EnumerateOutsideFences(lines))
        {
            var match = HeadingRegex.Match(line);

            if (match.Success)
                minimum = Math.Min(minimum, match.Groups[1].Value.Length);
        }

        return minimum == int.MaxValue ? 0 : minimum - 1;
    }

    private static IEnumerable<string> EnumerateOutsideFences(IEnumerable<string> lines)
    {
        var inFence = false;

        foreach (var line in lines)
        {
            if (FenceRegex.IsMatch(line))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
                yield return line;
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
    private static void AppendTableOfContents(Body body, IReadOnlyList<string> lines, int headingShift)
    {
        var entries = new List<(int Level, string Text)>();

        foreach (var line in EnumerateOutsideFences(lines))
        {
            var match = HeadingRegex.Match(line);

            if (!match.Success)
                continue;

            var level = Math.Max(1, match.Groups[1].Value.Length - headingShift);

            if (level <= 2)
                entries.Add((level, StripInlineMarkers(match.Groups[2].Value.Trim())));
        }

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

        /// <summary>Thụt lề đã gặp trong danh sách đang mở → bậc; danh sách 2 hay 4 dấu cách đều ra đúng bậc.</summary>
        public List<int> IndentStack { get; } = new();

        /// <summary>numId đang dùng cho từng bậc của danh sách đánh số hiện tại.</summary>
        public Dictionary<int, int> OrderedNumIds { get; } = new();

        private int _nextNumberingId = 100;

        public void CloseList()
        {
            IndentStack.Clear();
            OrderedNumIds.Clear();
        }

        public int ResolveLevel(int indent)
        {
            while (IndentStack.Count > 0 && indent < IndentStack[^1])
            {
                IndentStack.RemoveAt(IndentStack.Count - 1);
                OrderedNumIds.Remove(IndentStack.Count);
            }

            if (IndentStack.Count == 0 || indent > IndentStack[^1])
                IndentStack.Add(indent);

            return Math.Min(IndentStack.Count - 1, 2);
        }

        public int OrderedNumIdFor(int level)
        {
            if (OrderedNumIds.TryGetValue(level, out var existing))
                return existing;

            var numId = _nextNumberingId++;

            OrderedInstances.Add(BuildOrderedInstance(numId));
            OrderedNumIds[level] = numId;

            return numId;
        }
    }

    private static void AppendMarkdown(Body body, IReadOnlyList<string> lines, RenderContext context, int headingShift)
    {
        var i = 0;

        while (i < lines.Count)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                context.CloseList();
                i++;
                continue;
            }

            if (FenceRegex.IsMatch(line))
            {
                context.CloseList();
                i++;

                var code = new List<string>();

                while (i < lines.Count && !FenceRegex.IsMatch(lines[i]))
                    code.Add(lines[i++]);

                if (i < lines.Count)
                    i++;

                body.AppendChild(BuildCodeBlock(code));
                continue;
            }

            if (HorizontalRuleRegex.IsMatch(line))
            {
                context.CloseList();
                body.AppendChild(AccentBar());
                i++;
                continue;
            }

            var heading = HeadingRegex.Match(line);

            if (heading.Success)
            {
                context.CloseList();
                body.AppendChild(BuildHeading(heading.Groups[1].Value.Length - headingShift, heading.Groups[2].Value.Trim(), context));
                i++;
                continue;
            }

            if (IsTableStart(lines, i))
            {
                context.CloseList();

                var rows = new List<string>();

                while (i < lines.Count && lines[i].Contains('|') && !string.IsNullOrWhiteSpace(lines[i]))
                    rows.Add(lines[i++]);

                body.AppendChild(BuildTable(rows, context));
                body.AppendChild(Spacer(120));
                continue;
            }

            var quote = QuoteRegex.Match(line);

            if (quote.Success)
            {
                context.CloseList();

                var parts = new List<string>();

                while (i < lines.Count && QuoteRegex.IsMatch(lines[i]))
                    parts.Add(QuoteRegex.Match(lines[i++]).Groups[1].Value);

                body.AppendChild(BuildQuote(string.Join(" ", parts).Trim(), context));
                continue;
            }

            var bullet = BulletRegex.Match(line);

            if (bullet.Success)
            {
                var level = context.ResolveLevel(bullet.Groups[1].Value.Length);
                body.AppendChild(BuildListItem(bullet.Groups[2].Value, level, BulletNumId, context));
                i++;
                continue;
            }

            var ordered = OrderedRegex.Match(line);

            if (ordered.Success)
            {
                var level = context.ResolveLevel(ordered.Groups[1].Value.Length);
                body.AppendChild(BuildListItem(ordered.Groups[3].Value, level, context.OrderedNumIdFor(level), context));
                i++;
                continue;
            }

            // Đoạn văn: gom các dòng liền nhau lại như Markdown quy định, thay vì mỗi dòng một paragraph.
            var paragraph = new List<string>();

            while (i < lines.Count
                   && !string.IsNullOrWhiteSpace(lines[i])
                   && !HeadingRegex.IsMatch(lines[i])
                   && !BulletRegex.IsMatch(lines[i])
                   && !OrderedRegex.IsMatch(lines[i])
                   && !QuoteRegex.IsMatch(lines[i])
                   && !FenceRegex.IsMatch(lines[i])
                   && !HorizontalRuleRegex.IsMatch(lines[i])
                   && !IsTableStart(lines, i))
                paragraph.Add(lines[i++].Trim());

            context.CloseList();
            body.AppendChild(BuildBodyParagraph(string.Join(" ", paragraph), context));
        }
    }

    private static bool IsTableStart(IReadOnlyList<string> lines, int index) =>
        lines[index].TrimStart().StartsWith('|')
        && index + 1 < lines.Count
        && TableSeparatorRegex.IsMatch(lines[index + 1]);

    private static Paragraph BuildHeading(int level, string text, RenderContext context)
    {
        var styleId = "Heading" + Math.Clamp(level, 1, 4);

        var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = styleId }));
        paragraph.Append(ParseInline(text, new RunFormat(), context));

        return paragraph;
    }

    private static Paragraph BuildBodyParagraph(string text, RenderContext context)
    {
        var paragraph = new Paragraph();
        paragraph.Append(ParseInline(text, new RunFormat(), context));

        return paragraph;
    }

    private static Paragraph BuildListItem(string text, int level, int numId, RenderContext context)
    {
        var paragraph = new Paragraph(new ParagraphProperties(
            new ParagraphStyleId { Val = "ListParagraph" },
            new NumberingProperties(
                new NumberingLevelReference { Val = level },
                new NumberingId { Val = numId })));

        paragraph.Append(ParseInline(text.Trim(), new RunFormat(), context));

        return paragraph;
    }

    private static Paragraph BuildQuote(string text, RenderContext context)
    {
        var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Quote" }));
        paragraph.Append(ParseInline(text, new RunFormat { Italic = true, Color = Muted }, context));

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

    private static Table BuildTable(IReadOnlyList<string> rows, RenderContext context)
    {
        var cells = rows
            .Where(row => !TableSeparatorRegex.IsMatch(row))
            .Select(SplitTableRow)
            .Where(row => row.Count > 0)
            .ToList();

        var alignments = rows.Count > 1 && TableSeparatorRegex.IsMatch(rows[1])
            ? SplitTableRow(rows[1]).Select(ParseAlignment).ToList()
            : new List<JustificationValues>();

        var columnCount = cells.Count == 0 ? 1 : cells.Max(row => row.Count);

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

        for (var rowIndex = 0; rowIndex < cells.Count; rowIndex++)
        {
            var isHeader = rowIndex == 0;

            var row = new TableRow();

            if (isHeader)
                row.AppendChild(new TableRowProperties(new TableHeader()));

            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var text = columnIndex < cells[rowIndex].Count ? cells[rowIndex][columnIndex] : "";

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

                paragraph.Append(ParseInline(text, format, context));

                row.AppendChild(new TableCell(cellProperties, paragraph));
            }

            table.AppendChild(row);
        }

        return table;
    }

    private static List<string> SplitTableRow(string row)
    {
        var trimmed = row.Trim();

        if (trimmed.StartsWith('|'))
            trimmed = trimmed[1..];

        if (trimmed.EndsWith('|'))
            trimmed = trimmed[..^1];

        return trimmed.Split('|').Select(cell => cell.Trim()).ToList();
    }

    private static JustificationValues ParseAlignment(string spec)
    {
        var trimmed = spec.Trim();

        if (trimmed.StartsWith(':') && trimmed.EndsWith(':'))
            return JustificationValues.Center;

        return trimmed.EndsWith(':') ? JustificationValues.Right : JustificationValues.Left;
    }

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
    /// Dịch <c>**đậm**</c>, <c>*nghiêng*</c>, <c>`mã`</c>, <c>~~gạch~~</c> và <c>[chữ](link)</c> thành run
    /// Word. Không làm bước này thì mọi ký tự đánh dấu nằm nguyên trên trang giấy gửi cấp trên.
    /// </summary>
    private static List<OpenXmlElement> ParseInline(string text, RunFormat format, RenderContext context)
    {
        var elements = new List<OpenXmlElement>();
        AppendInline(elements, text ?? "", format, context);

        if (elements.Count == 0)
            elements.Add(BuildRun("", format));

        return elements;
    }

    private static void AppendInline(List<OpenXmlElement> elements, string text, RunFormat format, RenderContext context)
    {
        var buffer = new System.Text.StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0)
                return;

            elements.Add(BuildRun(buffer.ToString(), format));
            buffer.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length && !char.IsLetterOrDigit(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i++;
                continue;
            }

            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1);

                if (close > i)
                {
                    Flush();
                    elements.Add(BuildRun(text[(i + 1)..close], format with { Code = true }));
                    i = close;
                    continue;
                }
            }

            if (c == '[' && !format.Hyperlink)
            {
                var link = TryReadLink(text, i);

                if (link != null)
                {
                    Flush();
                    AppendHyperlink(elements, link.Value.Label, link.Value.Url, format, context);
                    i = link.Value.EndIndex;
                    continue;
                }
            }

            var marker = ReadMarker(text, i);

            if (marker != null)
            {
                var close = FindClosingMarker(text, i + marker.Length, marker);

                if (close > 0)
                {
                    Flush();

                    var inner = text[(i + marker.Length)..close];
                    var innerFormat = marker switch
                    {
                        "**" or "__" => format with { Bold = true },
                        "~~" => format with { Strike = true },
                        _ => format with { Italic = true }
                    };

                    AppendInline(elements, inner, innerFormat, context);
                    i = close + marker.Length - 1;
                    continue;
                }
            }

            buffer.Append(c);
        }

        Flush();
    }

    private static string? ReadMarker(string text, int index)
    {
        if (text.StartsWith2(index, "**"))
            return "**";

        if (text.StartsWith2(index, "~~"))
            return "~~";

        if (text.StartsWith2(index, "__"))
            return IsEmphasisBoundary(text, index - 1) ? "__" : null;

        var c = text[index];

        if (c == '*')
            return "*";

        // Gạch dưới giữa từ (snake_case, tên biến) không phải chữ nghiêng.
        if (c == '_' && IsEmphasisBoundary(text, index - 1))
            return "_";

        return null;
    }

    private static bool IsEmphasisBoundary(string text, int index) =>
        index < 0 || !char.IsLetterOrDigit(text[index]);

    private static int FindClosingMarker(string text, int start, string marker)
    {
        for (var i = start; i <= text.Length - marker.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (!text.StartsWith2(i, marker))
                continue;

            if (i == start)
                continue;

            return i;
        }

        return -1;
    }

    private static (string Label, string Url, int EndIndex)? TryReadLink(string text, int index)
    {
        var closeLabel = text.IndexOf(']', index + 1);

        if (closeLabel < 0 || closeLabel + 1 >= text.Length || text[closeLabel + 1] != '(')
            return null;

        var closeUrl = text.IndexOf(')', closeLabel + 2);

        if (closeUrl < 0)
            return null;

        return (text[(index + 1)..closeLabel], text[(closeLabel + 2)..closeUrl].Trim(), closeUrl);
    }

    private static void AppendHyperlink(List<OpenXmlElement> elements, string label, string url, RunFormat format, RenderContext context)
    {
        var linkFormat = format with { Hyperlink = true };

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            AppendInline(elements, label, linkFormat, context);
            return;
        }

        var relationship = context.Main.AddHyperlinkRelationship(uri, true);
        var hyperlink = new Hyperlink { Id = relationship.Id };

        var inner = new List<OpenXmlElement>();
        AppendInline(inner, label, linkFormat, context);
        hyperlink.Append(inner);

        elements.Add(hyperlink);
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

    private static string StripInlineMarkers(string text) =>
        text.Replace("**", "").Replace("__", "").Replace("`", "").Replace("~~", "").Trim();

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

    private static bool StartsWith2(this string text, int index, string value) =>
        index >= 0 && index + value.Length <= text.Length && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;
}
