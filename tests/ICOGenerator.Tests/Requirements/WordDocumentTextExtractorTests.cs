using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ICOGenerator.Services.Requirements;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Quy trình/biểu mẫu của phòng ban gần như luôn là .docx, nên nó phải đọc được như bảng tính: giữ THỨ TỰ
// tài liệu (đoạn văn xen bảng) và render bảng thành "ô | ô" để cấu trúc biểu mẫu không bị mất.
public class WordDocumentTextExtractorTests
{
    [Theory]
    [InlineData("quy-trinh.docx", null)]
    [InlineData("bieu-mau.DOCX", null)]
    [InlineData("mo-ta.docm", null)]
    [InlineData("x", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public void IsWordDocument_RecognizesExtensionsAndContentType(string fileName, string? contentType)
        => Assert.True(WordDocumentTextExtractor.IsWordDocument(contentType, fileName));

    [Theory]
    [InlineData("bang.xlsx")]
    [InlineData("anh.png")]
    [InlineData("tai-lieu.pdf")]
    public void IsWordDocument_RejectsOtherFormats(string fileName)
        => Assert.False(WordDocumentTextExtractor.IsWordDocument(null, fileName));

    [Fact]
    public void Extract_ParagraphsAndTable_KeepsDocumentOrderAndTableStructure()
    {
        var bytes = BuildDocx(body =>
        {
            body.AppendChild(Paragraph("Quy trình duyệt đơn nghỉ phép"));
            body.AppendChild(Table(
                new[] { "Trường", "Bắt buộc" },
                new[] { "Ngày bắt đầu", "Có" }));
            body.AppendChild(Paragraph("Quản lý duyệt trong 2 ngày."));
        });

        var text = WordDocumentTextExtractor.Extract(bytes);

        Assert.NotNull(text);
        Assert.Contains("Quy trình duyệt đơn nghỉ phép", text);
        Assert.Contains("Trường | Bắt buộc", text);
        Assert.Contains("Ngày bắt đầu | Có", text);

        // Thứ tự tài liệu: đoạn mở đầu → bảng → đoạn kết. Lấy riêng đoạn rồi riêng bảng sẽ dồn bảng xuống
        // cuối và làm mất mạch "mô tả rồi tới biểu mẫu".
        Assert.True(text!.IndexOf("Quy trình duyệt", StringComparison.Ordinal)
                    < text.IndexOf("Trường | Bắt buộc", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Trường | Bắt buộc", StringComparison.Ordinal)
                    < text.IndexOf("Quản lý duyệt trong 2 ngày", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_SkipsEmptyParagraphs()
    {
        var bytes = BuildDocx(body =>
        {
            body.AppendChild(Paragraph("Có nội dung"));
            body.AppendChild(Paragraph("   "));
            body.AppendChild(new Paragraph());
        });

        var text = WordDocumentTextExtractor.Extract(bytes);

        Assert.Equal("Có nội dung", text);
    }

    [Fact]
    public void Extract_CorruptFile_ReturnsNull_SoUploadStillKeepsTheOriginal()
        => Assert.Null(WordDocumentTextExtractor.Extract(new byte[] { 1, 2, 3, 4 }));

    [Fact]
    public void Extract_EmptyDocument_ReturnsNull()
        => Assert.Null(WordDocumentTextExtractor.Extract(BuildDocx(_ => { })));

    private static byte[] BuildDocx(Action<Body> fill)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());
            fill(body);
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static Paragraph Paragraph(string text) =>
        new(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Table Table(params string[][] rows)
    {
        var table = new Table();
        foreach (var row in rows)
        {
            var tableRow = new TableRow();
            foreach (var cell in row)
                tableRow.AppendChild(new TableCell(Paragraph(cell)));
            table.AppendChild(tableRow);
        }
        return table;
    }
}
