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

    // Tài liệu kỹ thuật hay nhét thông tin quan trọng vào ảnh (screenshot, sơ đồ). Hình đủ lớn phải được
    // ghi ra figure-{n} cho model vision, có mốc [Hình n] trong text đúng vị trí; icon/trang trí nhỏ bị lọc.
    [Fact]
    public void Extract_EmbeddedImages_WritesLargeFigures_SkipsIcons_AndInsertsMarkers()
    {
        var dir = NewTempDir();
        try
        {
            var bytes = BuildDocxWithImages((body, addImage) =>
            {
                body.AppendChild(Paragraph("Màn hình nhập đơn như sau:"));
                addImage(body, FakePng(800, 600)); // screenshot thật
                body.AppendChild(Paragraph("Icon trạng thái:"));
                addImage(body, FakePng(16, 16));   // icon trang trí — phải bị lọc
                body.AppendChild(Paragraph("Hết."));
            });

            var result = WordDocumentTextExtractor.Extract(bytes, dir);

            Assert.Equal(1, result.ImageCount);
            Assert.True(File.Exists(Path.Combine(dir, "figure-1.png")));
            Assert.NotNull(result.Text);
            Assert.Contains("[Hình 1", result.Text);
            // Mốc phải nằm SAU đoạn mô tả nó minh hoạ và TRƯỚC phần sau — model đối chiếu hình với ngữ cảnh.
            Assert.True(result.Text!.IndexOf("Màn hình nhập đơn", StringComparison.Ordinal)
                        < result.Text.IndexOf("[Hình 1", StringComparison.Ordinal));
            Assert.True(result.Text.IndexOf("[Hình 1", StringComparison.Ordinal)
                        < result.Text.IndexOf("Icon trạng thái", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Word toàn ảnh (vd bản scan dán vào từng trang): text = null nhưng hình vẫn phải lấy được — nội dung
    // đi bằng đường vision thay vì mất trắng.
    [Fact]
    public void Extract_ImageOnlyDocument_ReturnsNullText_ButExtractsImages()
    {
        var dir = NewTempDir();
        try
        {
            var bytes = BuildDocxWithImages((body, addImage) => addImage(body, FakePng(1000, 700)));

            var result = WordDocumentTextExtractor.Extract(bytes, dir);

            Assert.Null(result.Text);
            Assert.Equal(1, result.ImageCount);
            Assert.True(File.Exists(Path.Combine(dir, "figure-1.png")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Vượt trần thì giữ các hình LỚN nhất (screenshot thật thường lớn hơn hình phụ), không lấy quá MaxImages.
    [Fact]
    public void Extract_MoreImagesThanCap_KeepsOnlyTheLargest()
    {
        var dir = NewTempDir();
        try
        {
            var bytes = BuildDocxWithImages((body, addImage) =>
            {
                for (var i = 0; i < WordDocumentTextExtractor.MaxImages + 3; i++)
                    addImage(body, FakePng(400 + i, 300));
            });

            var result = WordDocumentTextExtractor.Extract(bytes, dir);

            Assert.Equal(WordDocumentTextExtractor.MaxImages, result.ImageCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Extract(bytes) kiểu cũ (không có thư mục đích) giữ nguyên hành vi: chỉ text, không mốc [Hình n].
    [Fact]
    public void Extract_WithoutTargetDir_ExtractsTextOnly_NoMarkers()
    {
        var bytes = BuildDocxWithImages((body, addImage) =>
        {
            body.AppendChild(Paragraph("Chỉ cần text"));
            addImage(body, FakePng(800, 600));
        });

        var text = WordDocumentTextExtractor.Extract(bytes);

        Assert.Equal("Chỉ cần text", text);
    }

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

    // Như BuildDocx nhưng callback nhận thêm hàm addImage(body, pngBytes): tạo ImagePart và chèn một đoạn
    // văn chứa Drawing → Blip trỏ tới part đó (extractor tìm Blip ở mọi độ sâu nên cây tối giản là đủ).
    private static byte[] BuildDocxWithImages(Action<Body, Action<Body, byte[]>> fill)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());
            fill(body, (b, png) =>
            {
                var part = main.AddImagePart("image/png");
                using (var stream = new MemoryStream(png))
                    part.FeedData(stream);
                b.AppendChild(new Paragraph(new Run(new DocumentFormat.OpenXml.Wordprocessing.Drawing(
                    new DocumentFormat.OpenXml.Drawing.Blip { Embed = main.GetIdOfPart(part) }))));
            });
            main.Document.Save();
        }
        return ms.ToArray();
    }

    // PNG "đủ thật" cho extractor: chữ ký + IHDR có width/height đúng (extractor chỉ đọc header, không decode).
    private static byte[] FakePng(int width, int height)
    {
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        bytes[11] = 13; // độ dài IHDR
        System.Text.Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), (uint)height);
        return bytes;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "word-extract-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
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
