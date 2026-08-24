using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ICOGenerator.Services.Requirements.Templates;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Bản .docx của Product Brief / AI Design Spec / User Stories là thứ người dùng GỬI CHO CẤP TRÊN duyệt,
// nên nó phải là tài liệu Word thật chứ không phải bản đổ thô Markdown. Các test chốt: (1) không còn ký
// tự đánh dấu Markdown nào lọt ra mặt giấy; (2) heading mang style Word (Word tự dựng mục lục được, và
// DocxTemplateWriter.ExtractHtml render đúng cấp); (3) danh sách dùng numbering thật, bảng Markdown thành
// bảng Word; (4) trang bìa/header/footer/số trang có mặt; (5) nội dung rỗng hay hỏng vẫn ra file mở được.
public class MarkdownDocxWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "md-docx-" + Guid.NewGuid().ToString("N"));

    private static readonly DocxDocumentMeta Meta =
        new("Product Brief", "JD Library 7", "draft", new DateTime(2026, 3, 17));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string Write(string markdown)
    {
        var path = Path.Combine(_dir, "ProductBrief.docx");
        MarkdownDocxWriter.Create(path, Meta, markdown);

        return path;
    }

    private static (Body Body, WordprocessingDocument Doc) Open(string path)
    {
        var doc = WordprocessingDocument.Open(path, false);

        return (doc.MainDocumentPart!.Document!.Body!, doc);
    }

    [Fact]
    public void Create_HeadingLines_BecomeWordHeadingStyles()
    {
        var path = Write("""
            # JD Library 7

            ## Sản phẩm này là gì?

            Ứng dụng quản lý mô tả công việc.

            ### Chi tiết
            """);

        var (body, doc) = Open(path);
        using var _ = doc;

        var styles = body.Descendants<Paragraph>()
            .Select(p => (Style: p.ParagraphProperties?.ParagraphStyleId?.Val?.Value, Text: p.InnerText))
            .ToList();

        // Tên sản phẩm (#) đã lên bìa nên các mục ## được nâng lên Heading1: giữ nguyên bậc thì cả tài
        // liệu không có Heading1 nào và khung điều hướng của Word thụt vào một cấp vô cớ.
        Assert.Contains(styles, x => x.Style == "Heading1" && x.Text == "Sản phẩm này là gì?");
        Assert.Contains(styles, x => x.Style == "Heading2" && x.Text == "Chi tiết");

        // Dòng "# Tên sản phẩm" mở đầu được đưa lên trang bìa, không lặp lại một lần nữa ở thân bài —
        // và khi nó trùng tên dự án thì trang bìa cũng chỉ in một lần.
        Assert.Equal(1, styles.Count(x => x.Text == "JD Library 7"));
    }

    [Fact]
    public void Create_HeadingsAlreadyAtTopLevel_AreNotPromotedAgain()
    {
        var path = Write("""
            # Phần một

            # Phần hai

            ## Mục con
            """);

        var (body, doc) = Open(path);
        using var _ = doc;

        var styles = body.Descendants<Paragraph>()
            .Select(p => (Style: p.ParagraphProperties?.ParagraphStyleId?.Val?.Value, Text: p.InnerText))
            .ToList();

        Assert.Contains(styles, x => x.Style == "Heading1" && x.Text == "Phần hai");
        Assert.Contains(styles, x => x.Style == "Heading2" && x.Text == "Mục con");
    }

    [Fact]
    public void Create_RichContent_IsSchemaValidOpenXml()
    {
        var path = Write("""
            ## Mục có đủ thứ

            Đoạn văn có **đậm**, *nghiêng*, `mã` và [liên kết](https://example.com/jd).

            > Ghi chú của người dùng.

            1. Bước một
            2. Bước hai

            | Vai trò | Quyền |
            | :--- | ---: |
            | Manager | Tạo JD |

            ```json
            { "code": "HcP-JD-001" }
            ```

            ---
            """);

        using var doc = WordprocessingDocument.Open(path, false);

        var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(doc).ToList();

        // Word từ chối mở file sai lược đồ; validator là chốt duy nhất bắt được điều đó ở đây.
        Assert.Empty(errors.Select(e => $"{e.Path?.XPath}: {e.Description}"));
    }

    [Fact]
    public void Create_MarkdownMarkers_DoNotSurviveOnThePage()
    {
        var path = Write("""
            ## Tính năng

            - **Tạo JD** cho orgUnit
              - *Hoàn thành khi: Manager tạo được JD.*
            - Mã JD theo cú pháp `HcP-JD-XXX`

            | Vai trò | Quyền |
            | --- | --- |
            | Manager | Tạo JD |
            """);

        var (body, doc) = Open(path);
        using var _ = doc;

        var text = body.InnerText;

        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("`", text);
        Assert.DoesNotContain("| ---", text);
        Assert.DoesNotContain("## ", text);
        Assert.Contains("Tạo JD", text);
        Assert.Contains("HcP-JD-XXX", text);
    }

    [Fact]
    public void Create_BoldAndItalic_BecomeRunFormatting()
    {
        var path = Write("- **Tạo JD** cho orgUnit và *ghi chú*");

        var (body, doc) = Open(path);
        using var _ = doc;

        var runs = body.Descendants<Run>().ToList();

        Assert.Contains(runs, r => r.InnerText == "Tạo JD" && r.RunProperties?.Bold != null);
        Assert.Contains(runs, r => r.InnerText == "ghi chú" && r.RunProperties?.Italic != null);
    }

    [Fact]
    public void Create_Lists_UseRealNumbering()
    {
        var path = Write("""
            ## Luồng

            1. Manager tạo JD
            2. HRBP verify

            ## Quy tắc

            - Chỉ JD Available mới được assign
              - Mỗi lần assign một nhân viên
            """);

        var (body, doc) = Open(path);
        using var _ = doc;

        var numbered = body.Descendants<Paragraph>()
            .Where(p => p.ParagraphProperties?.NumberingProperties != null)
            .ToList();

        Assert.Equal(4, numbered.Count);
        Assert.All(numbered, p => Assert.Equal("ListParagraph", p.ParagraphProperties!.ParagraphStyleId!.Val!.Value));

        // Bậc hai của danh sách thụt lề phải nằm ở level 1, nếu không nó trông ngang hàng với mục cha.
        var nested = numbered.Single(p => p.InnerText.Contains("Mỗi lần assign"));
        Assert.Equal(1, nested.ParagraphProperties!.NumberingProperties!.NumberingLevelReference!.Val!.Value);

        Assert.NotNull(doc.MainDocumentPart!.NumberingDefinitionsPart);
    }

    [Fact]
    public void Create_TwoOrderedLists_EachRestartsAtOne()
    {
        var path = Write("""
            ### Luồng 1

            1. Bước một
            2. Bước hai

            ### Luồng 2

            1. Bước một
            """);

        var (_, doc) = Open(path);
        using var _1 = doc;

        var numbering = doc.MainDocumentPart!.NumberingDefinitionsPart!.Numbering!;

        // Mỗi danh sách một instance có startOverride, nếu không "Luồng 2" đánh số tiếp thành 3.
        var restarts = numbering.Descendants<NumberingInstance>()
            .Count(x => x.Descendants<StartOverrideNumberingValue>().Any());

        Assert.Equal(2, restarts);
    }

    [Fact]
    public void Create_MarkdownTable_BecomesWordTable()
    {
        var path = Write("""
            | Vai trò | Quyền |
            | --- | --- |
            | Manager | Tạo JD |
            | HRBP | Verify JD |
            """);

        var (body, doc) = Open(path);
        using var _ = doc;

        var table = Assert.Single(body.Descendants<Table>(), t => t.Descendants<TableRow>().Count() == 3);
        var rows = table.Descendants<TableRow>().ToList();

        Assert.Equal(2, rows[0].Descendants<TableCell>().Count());
        Assert.Equal("Vai trò", rows[0].Descendants<TableCell>().First().InnerText);
        Assert.Equal("Verify JD", rows[2].Descendants<TableCell>().Last().InnerText);

        // Dòng đầu là dòng tiêu đề: lặp lại khi bảng tràn trang, nếu không trang sau là một khối ô không đầu đề.
        Assert.NotNull(rows[0].Descendants<TableHeader>().SingleOrDefault());
    }

    [Fact]
    public void Create_AlwaysHasCoverHeaderFooterAndPageNumber()
    {
        var path = Write("## Mục\n\nNội dung.");

        var (body, doc) = Open(path);
        using var _ = doc;

        Assert.Contains("Product Brief", body.InnerText);
        Assert.Contains("JD Library 7", body.InnerText);
        Assert.Contains("17/03/2026", body.InnerText);
        Assert.Contains("Bản nháp", body.InnerText);

        var footer = Assert.Single(doc.MainDocumentPart!.FooterParts, f => f.Footer!.InnerText.Contains("Trang"));
        Assert.Contains("PAGE", string.Concat(footer.Footer!.Descendants<FieldCode>().Select(x => x.Text)));

        Assert.Contains(doc.MainDocumentPart.HeaderParts, h => h.Header!.InnerText.Contains("Product Brief"));

        // Trang bìa đứng riêng: có ngắt trang và sectPr bật titlePg để bìa không đeo header/footer.
        Assert.Contains(body.Descendants<Break>(), b => b.Type != null && b.Type == BreakValues.Page);
        Assert.NotNull(body.Descendants<SectionProperties>().Single().GetFirstChild<TitlePage>());
    }

    [Fact]
    public void Create_ManyHeadings_AddsTableOfContentsField()
    {
        var path = Write("""
            ## Một

            ## Hai

            ## Ba
            """);

        var (body, doc) = Open(path);
        using var _ = doc;

        Assert.Contains("Mục lục", body.InnerText);

        var fields = string.Concat(body.Descendants<FieldCode>().Select(x => x.Text));
        Assert.Contains("TOC", fields);

        // Mục lục có sẵn nội dung tĩnh: công cụ không cập nhật field (Google Docs, khung xem trước) vẫn
        // đọc được danh sách mục thay vì một trang trắng.
        Assert.Contains(body.Descendants<Paragraph>(), p =>
            p.ParagraphProperties?.ParagraphStyleId?.Val?.Value == "TOC1" && p.InnerText.Contains("Một"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Chỉ một dòng, không heading nào.")]
    public void Create_EmptyOrPlainContent_StillProducesReadableDocument(string? markdown)
    {
        var path = Write(markdown!);

        var (body, doc) = Open(path);
        using var _ = doc;

        Assert.Contains("Product Brief", body.InnerText);
        Assert.DoesNotContain("Mục lục", body.InnerText);
    }

    [Fact]
    public void Create_ExistingFile_IsReplacedAtomically()
    {
        var path = Write("## Bản cũ");
        Write("## Bản mới");

        var (body, doc) = Open(path);
        using var _ = doc;

        Assert.Contains("Bản mới", body.InnerText);
        Assert.DoesNotContain("Bản cũ", body.InnerText);
        Assert.False(File.Exists(path + ".tmp"));
    }
}
