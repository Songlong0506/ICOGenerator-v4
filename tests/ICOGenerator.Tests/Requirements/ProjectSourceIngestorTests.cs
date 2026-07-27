using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace ICOGenerator.Tests.Requirements;

// Tài liệu nguồn (ảnh/PDF) phải được lưu xuống workspace + bóc text PDF, và SourceContextBuilder chỉ kèm ảnh
// khi model hỗ trợ vision. Các test này không phụ thuộc native PDFium (chỉ kiểm tra ảnh + bóc text PDF + validate).
public class ProjectSourceIngestorTests : IDisposable
{
    // PNG 1x1 hợp lệ (transparent) — đủ để kiểm tra luồng ingest ảnh mà không cần lib ảnh.
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly string _root;

    public ProjectSourceIngestorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ico-src-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private ProjectSourceIngestor NewIngestor()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = _root })
            .Build();
        var storage = new LocalArtifactStorage(new WorkspacePathResolver(config), NullLogger<LocalArtifactStorage>.Instance);
        return new ProjectSourceIngestor(storage, config, NullLogger<ProjectSourceIngestor>.Instance);
    }

    [Fact]
    public async Task IngestAsync_Image_StoresFile_AndIsVisionSource()
    {
        var ingestor = NewIngestor();
        using var ms = new MemoryStream(OnePixelPng);

        var entity = await ingestor.IngestAsync(
            Guid.NewGuid(), "proj-key", "shot.png", "image/png", OnePixelPng.Length, ms, "tester");

        Assert.Equal(SourceFileKind.Image, entity.Kind);
        Assert.True(entity.IsVisionSource);
        Assert.Equal("image/png", entity.ContentType);
        Assert.True(File.Exists(entity.StoredPath));
        Assert.Null(entity.ExtractedText);
    }

    [Fact]
    public async Task IngestAsync_TextPdf_ExtractsText()
    {
        var pdf = BuildTextPdf("Yeu cau he thong quan ly dao tao noi bo");
        var ingestor = NewIngestor();
        using var ms = new MemoryStream(pdf);

        var entity = await ingestor.IngestAsync(
            Guid.NewGuid(), "proj-key", "spec.pdf", "application/pdf", pdf.Length, ms, null);

        Assert.Equal(SourceFileKind.Pdf, entity.Kind);
        Assert.Equal(1, entity.PageCount);
        Assert.False(string.IsNullOrWhiteSpace(entity.ExtractedText));
        Assert.Contains("quan ly dao tao", entity.ExtractedText);
    }

    [Fact]
    public async Task IngestAsync_UnsupportedType_Throws()
    {
        var ingestor = NewIngestor();
        using var ms = new MemoryStream(new byte[] { 1, 2, 3 });

        await Assert.ThrowsAsync<SourceFileValidationException>(() =>
            ingestor.IngestAsync(Guid.NewGuid(), "proj-key", "malware.exe", "application/octet-stream", 3, ms, null));
    }

    [Fact]
    public async Task IngestAsync_OversizedFile_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentWorkspace:RootPath"] = _root,
                ["Llm:SourceUpload:MaxFileBytes"] = "10",
            })
            .Build();
        var ingestor = new ProjectSourceIngestor(
            new LocalArtifactStorage(new WorkspacePathResolver(config), NullLogger<LocalArtifactStorage>.Instance), config, NullLogger<ProjectSourceIngestor>.Instance);
        using var ms = new MemoryStream(OnePixelPng);

        await Assert.ThrowsAsync<SourceFileValidationException>(() =>
            ingestor.IngestAsync(Guid.NewGuid(), "proj-key", "shot.png", "image/png", OnePixelPng.Length, ms, null));
    }

    [Fact]
    public void SourceContextBuilder_IncludesImage_OnlyWhenVision()
    {
        var imgPath = Path.Combine(_root, "img.png");
        File.WriteAllBytes(imgPath, OnePixelPng);
        var source = new ICOGenerator.Domain.ProjectSourceFile
        {
            Kind = SourceFileKind.Image,
            FileName = "img.png",
            ContentType = "image/png",
            StoredPath = imgPath,
            IsVisionSource = true,
        };
        var config = new ConfigurationBuilder().Build();
        var builder = new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance);

        var withVision = builder.Build(new[] { source }, modelSupportsVision: true);
        var noVision = builder.Build(new[] { source }, modelSupportsVision: false);

        Assert.Contains(withVision, c => c is Microsoft.Extensions.AI.DataContent);
        Assert.DoesNotContain(noVision, c => c is Microsoft.Extensions.AI.DataContent);
        // Cả hai đều phải có phần text (tiêu đề nguồn) để model biết có tài liệu đính kèm.
        Assert.Contains(noVision, c => c is Microsoft.Extensions.AI.TextContent);

        // Text vision: được phép mời model "xem nội dung ảnh" vì ảnh THẬT SỰ được gửi kèm.
        var visionText = string.Concat(withVision.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));
        Assert.Contains("xem nội dung ảnh", visionText);

        // Text không-vision KHÔNG được mời "xem nội dung ảnh" (ảnh không gửi kèm → model sẽ bịa); phải nói thẳng
        // là không đọc được để BA hỏi người dùng gõ lại thay vì tự suy đoán.
        var noVisionText = string.Concat(noVision.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));
        Assert.DoesNotContain("xem nội dung ảnh", noVisionText);
        Assert.Contains("KHÔNG đọc được ảnh", noVisionText);
    }


    [Fact]
    public async Task IngestAsync_WordDocument_ExtractsTextAndIsNotAVisionSource()
    {
        // Quy trình/biểu mẫu phòng ban thường là .docx — trước đây bị chặn ngay ở cổng validate nên người
        // dùng phải copy tay sang chat (mất cấu trúc bảng).
        var ingestor = NewIngestor();
        var docx = BuildDocx("Quy trình duyệt đơn", "Trường", "Bắt buộc");
        using var ms = new MemoryStream(docx);

        var entity = await ingestor.IngestAsync(
            Guid.NewGuid(), "proj-key", "quy-trinh.docx", null, docx.Length, ms, "tester");

        Assert.Equal(SourceFileKind.Document, entity.Kind);
        Assert.False(entity.IsVisionSource);
        Assert.True(File.Exists(entity.StoredPath));
        Assert.NotNull(entity.ExtractedText);
        Assert.Contains("Quy trình duyệt đơn", entity.ExtractedText);
        Assert.Contains("Trường | Bắt buộc", entity.ExtractedText);
    }

    [Fact]
    public async Task IngestAsync_TextPdf_HasNoScannedPageImages()
    {
        // PDF bóc được text thì không có trang scan nào ⇒ không lấy ảnh trang, không thành nguồn vision.
        var ingestor = NewIngestor();
        // Chữ ASCII: font Standard14 của PdfPig không dựng được ký tự tiếng Việt có dấu.
        var pdf = BuildTextPdf("Leave request: employee submits, manager approves.");
        using var ms = new MemoryStream(pdf);

        var entity = await ingestor.IngestAsync(
            Guid.NewGuid(), "proj-key", "don.pdf", "application/pdf", pdf.Length, ms, "tester");

        Assert.Equal(SourceFileKind.Pdf, entity.Kind);
        Assert.Equal(0, entity.ScannedPageImageCount);
        Assert.False(entity.IsVisionSource);
    }

    [Fact]
    public void SourceContextBuilder_ScannedPdfWithPageImages_AttachesThemInPageOrder()
    {
        // Ảnh trang phải đi theo SỐ TRANG: một biểu mẫu nhiều trang đọc sai thứ tự là hiểu sai quy trình.
        var dir = Path.Combine(_root, "scan-src");
        Directory.CreateDirectory(dir);
        foreach (var page in new[] { 1, 2, 10 })
            File.WriteAllBytes(Path.Combine(dir, $"page-{page}.png"), OnePixelPng);

        var source = new ICOGenerator.Domain.ProjectSourceFile
        {
            Kind = SourceFileKind.Pdf,
            FileName = "bieu-mau-scan.pdf",
            ContentType = "application/pdf",
            StoredPath = Path.Combine(dir, "bieu-mau-scan.pdf"),
            ScannedPageImageCount = 3,
            IsVisionSource = true
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var builder = new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance);

        var withVision = builder.Build(new[] { source }, modelSupportsVision: true);
        var noVision = builder.Build(new[] { source }, modelSupportsVision: false);

        Assert.Equal(3, withVision.OfType<Microsoft.Extensions.AI.DataContent>().Count());
        Assert.DoesNotContain(noVision, c => c is Microsoft.Extensions.AI.DataContent);

        // Có ảnh gửi kèm ⇒ KHÔNG được nói "nội dung bị bỏ qua" (câu đó khiến BA đi hỏi lại thứ nó đang cầm).
        var visionText = string.Concat(withVision.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));
        Assert.Contains("gửi kèm dưới dạng ẢNH", visionText);
        Assert.DoesNotContain("nội dung bị bỏ qua", visionText);
    }

    private static byte[] BuildDocx(string paragraph, params string[] tableCells)
    {
        using var ms = new MemoryStream();
        using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                   ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
            var body = main.Document.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Body());
            body.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                new DocumentFormat.OpenXml.Wordprocessing.Run(
                    new DocumentFormat.OpenXml.Wordprocessing.Text(paragraph))));

            var row = new DocumentFormat.OpenXml.Wordprocessing.TableRow();
            foreach (var cell in tableCells)
                row.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.TableCell(
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.Run(
                            new DocumentFormat.OpenXml.Wordprocessing.Text(cell)))));
            body.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Table(row));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static byte[] BuildTextPdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        page.AddText(text, 12, new PdfPoint(50, 700), font);
        return builder.Build();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
