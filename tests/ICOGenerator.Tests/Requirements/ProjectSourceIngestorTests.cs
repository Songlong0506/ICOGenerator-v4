using DocumentFormat.OpenXml.Packaging;
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

        Assert.Contains(withVision.Contents, c => c is Microsoft.Extensions.AI.DataContent);
        Assert.DoesNotContain(noVision.Contents, c => c is Microsoft.Extensions.AI.DataContent);
        // Cả hai đều phải có phần text (tiêu đề nguồn) để model biết có tài liệu đính kèm.
        Assert.Contains(noVision.Contents, c => c is Microsoft.Extensions.AI.TextContent);

        // Text vision: được phép mời model "xem nội dung ảnh" vì ảnh THẬT SỰ được gửi kèm.
        var visionText = string.Concat(withVision.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));
        Assert.Contains("xem nội dung ảnh", visionText);

        // Text không-vision KHÔNG được mời "xem nội dung ảnh" (ảnh không gửi kèm → model sẽ bịa); phải nói thẳng
        // là không đọc được để BA hỏi người dùng gõ lại thay vì tự suy đoán.
        var noVisionText = string.Concat(noVision.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));
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
    public async Task IngestAsync_WordDocumentWithEmbeddedImage_ExtractsFigure_AndIsVisionSource()
    {
        // Tài liệu kỹ thuật hay nhét screenshot/sơ đồ vào ảnh nhúng — phải lấy ra figure-{n}.png cạnh file
        // gốc cho model vision, thay vì chỉ bóc text rồi mất trắng phần hình.
        var ingestor = NewIngestor();
        var docx = BuildDocxWithLargeImage("Màn hình nhập kho như sau:");
        using var ms = new MemoryStream(docx);

        var entity = await ingestor.IngestAsync(
            Guid.NewGuid(), "proj-key", "tai-lieu-ky-thuat.docx", null, docx.Length, ms, "tester");

        Assert.Equal(SourceFileKind.Document, entity.Kind);
        Assert.Equal(1, entity.ScannedPageImageCount);
        Assert.True(entity.IsVisionSource);
        Assert.NotNull(entity.ExtractedText);
        Assert.Contains("[Hình 1", entity.ExtractedText);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(entity.StoredPath)!, "figure-1.png")));
    }

    [Fact]
    public void SourceContextBuilder_WordWithFigures_AttachesThemInOrder_OnlyWhenVision()
    {
        // Hình bóc từ Word phải đi theo SỐ THỨ TỰ (figure-10 không nhảy lên trước figure-2) và chỉ khi
        // model có vision — như ảnh trang PDF scan.
        var dir = Path.Combine(_root, "word-src");
        Directory.CreateDirectory(dir);
        foreach (var n in new[] { 1, 2, 10 })
            File.WriteAllBytes(Path.Combine(dir, $"figure-{n}.png"), OnePixelPng);

        var source = new ICOGenerator.Domain.ProjectSourceFile
        {
            Kind = SourceFileKind.Document,
            FileName = "tai-lieu.docx",
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            StoredPath = Path.Combine(dir, "tai-lieu.docx"),
            ExtractedText = "Quy trình nhập kho. [Hình 1 — trích từ tài liệu, gửi kèm dưới dạng ảnh]",
            ScannedPageImageCount = 3,
            IsVisionSource = true
        };

        var config = new ConfigurationBuilder().Build();
        var builder = new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance);

        var withVision = builder.Build(new[] { source }, modelSupportsVision: true);
        var noVision = builder.Build(new[] { source }, modelSupportsVision: false);

        Assert.Equal(3, withVision.Contents.OfType<Microsoft.Extensions.AI.DataContent>().Count());
        Assert.DoesNotContain(noVision.Contents, c => c is Microsoft.Extensions.AI.DataContent);

        // Vision: được mời xem ảnh đính kèm; không vision: phải nói thẳng hình không được gửi để model
        // không bịa nội dung theo các mốc [Hình n] trong text.
        var visionText = string.Concat(withVision.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));
        Assert.Contains("xem nội dung ảnh đính kèm", visionText);
        var noVisionText = string.Concat(noVision.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));
        Assert.Contains("KHÔNG đọc được ảnh", noVisionText);
    }

    [Fact]
    public void SourceContextBuilder_DefaultCap_FitsEveryFigureAWordDocCanYield()
    {
        // Trần mặc định mỗi lượt gọi phải >= trần hình bóc ra từ MỘT file Word. Lệch nhau (trước đây 6 với
        // 12) nghĩa là tài liệu nào cũng mất nửa số hình ngay ở cấu hình mặc định.
        var dir = NewSourceDir("word-full", WordDocumentTextExtractor.MaxImages);
        var source = WordSource(dir, WordDocumentTextExtractor.MaxImages);
        var builder = NewBuilder();

        var built = builder.Build(new[] { source }, modelSupportsVision: true);

        Assert.Equal(WordDocumentTextExtractor.MaxImages,
            built.Contents.OfType<Microsoft.Extensions.AI.DataContent>().Count());
        Assert.Contains(source.Id, built.FullyAttachedSourceIds);
    }

    [Fact]
    public void SourceContextBuilder_WhenCapCutsFigures_SaysHowManyActuallyWent()
    {
        // Đây là lỗi gốc: câu ghi chú nói "kèm 12 hình" trong khi chỉ 6 tấm đi kèm ⇒ model được mời đọc
        // những tấm nó không hề nhận được và sẽ bịa nội dung cho các mốc [Hình n] còn lại.
        var dir = NewSourceDir("word-capped", 3);
        var source = WordSource(dir, 3);
        var builder = NewBuilder(maxImagesPerCall: 2);

        var built = builder.Build(new[] { source }, modelSupportsVision: true);
        var text = string.Concat(built.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));

        Assert.Equal(2, built.Contents.OfType<Microsoft.Extensions.AI.DataContent>().Count());
        Assert.Contains("kèm 2/3", text);
        Assert.DoesNotContain("kèm 3 hình", text);
        // Chưa nhìn đủ hình thì KHÔNG được khóa lại thành mô tả chữ — lượt sau còn phải gửi nốt.
        Assert.Empty(built.FullyAttachedSourceIds);
    }

    [Fact]
    public void SourceContextBuilder_WithVisionSummary_SendsTextInsteadOfImages()
    {
        // Ảnh vốn đi kèm LẠI ở mọi lượt chat (mỗi lượt là một request mới). Có bản mô tả hình bằng chữ rồi
        // thì ảnh phải ngừng đi — đây chính là chỗ cắt chi phí của cả cơ chế.
        var dir = NewSourceDir("word-summarized", 3);
        var source = WordSource(dir, 3);
        source.VisionSummary = "[Hình 1] — Màn hình Belt Type: cột Belt Type, Belt Size, Action.";
        var builder = NewBuilder();

        var built = builder.Build(new[] { source }, modelSupportsVision: true);
        var text = string.Concat(built.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));

        Assert.Empty(built.Contents.OfType<Microsoft.Extensions.AI.DataContent>());
        Assert.Contains("Màn hình Belt Type", text);
        Assert.Contains("KHÔNG gửi lại", text);
        // Không mời model "xem ảnh đính kèm" khi chẳng có ảnh nào đính kèm.
        Assert.DoesNotContain("xem nội dung ảnh đính kèm", text);
        // Đã có mô tả rồi thì không ghi đè bằng mô tả mới của lượt sau.
        Assert.Empty(built.FullyAttachedSourceIds);
    }

    [Fact]
    public void SourceContextBuilder_SummarizedSource_FreesImageBudgetForTheRest()
    {
        // Hệ quả có chủ đích: project nhiều tài liệu tự cuốn chiếu — file đã mô tả xong nhường hạn mức ảnh
        // cho file chưa được nhìn, thay vì kẹt mãi ở mấy file upload trước.
        var describedDir = NewSourceDir("first", 2);
        var described = WordSource(describedDir, 2);
        described.VisionSummary = "[Hình 1] — sơ đồ duyệt đơn.";
        described.CreatedAt = DateTime.UtcNow.AddMinutes(-5);

        var pendingDir = NewSourceDir("second", 2);
        var pending = WordSource(pendingDir, 2);
        pending.FileName = "tai-lieu-2.docx";

        var builder = NewBuilder(maxImagesPerCall: 2);
        var built = builder.Build(new[] { described, pending }, modelSupportsVision: true);

        Assert.Equal(2, built.Contents.OfType<Microsoft.Extensions.AI.DataContent>().Count());
        Assert.Equal(new[] { pending.Id }, built.FullyAttachedSourceIds);
    }

    // BẢNG CỘT đã chốt phải đi kèm nguồn ở MỌI lượt chat sau. Không có khối này thì bảng chỉ là một màn
    // bấm đẹp: BA vẫn hỏi lại đúng các cột người dùng vừa ngồi giải nghĩa, và vẫn dựng yêu cầu trên những
    // cột họ đã bỏ tích.
    [Fact]
    public void SourceContextBuilder_AttachesTheConfirmedColumnMap_NextToTheFileItBelongsTo()
    {
        var source = new ICOGenerator.Domain.ProjectSourceFile
        {
            Id = Guid.NewGuid(),
            FileName = "KeHoach.xlsx",
            Kind = SourceFileKind.Spreadsheet,
            ExtractedText = "Global ID | Item Title | Revision Number",
            ColumnMap = """
                [{"FileName":"KeHoach.xlsx","Column":"Global ID","Meaning":"mã số nhân viên","Used":true},
                 {"FileName":"KeHoach.xlsx","Column":"Revision Number","Meaning":"phiên bản nội dung","Used":false}]
                """
        };

        var built = NewBuilder().Build(new[] { source }, modelSupportsVision: false);
        var text = string.Concat(built.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));

        Assert.Contains("đã được NGƯỜI DÙNG CHỐT", text);
        Assert.Contains("Global ID: mã số nhân viên", text);
        // Vế "không dùng" cũng phải đi kèm — đó là thứ chặn BA hỏi thêm về cột của hệ cũ.
        Assert.Contains("Cột KHÔNG dùng", text);
        Assert.Contains("Revision Number", text);
    }

    // Chưa chốt bảng nào ⇒ không thêm khối nào. Một dòng "chưa chốt bảng cột" trong ngữ cảnh chỉ mời model
    // đi giục người dùng đi tích bảng, giữa lúc nó đang phải phỏng vấn.
    [Fact]
    public void SourceContextBuilder_SaysNothingAboutColumns_WhenTheMapIsNotConfirmedYet()
    {
        var source = new ICOGenerator.Domain.ProjectSourceFile
        {
            Id = Guid.NewGuid(),
            FileName = "KeHoach.xlsx",
            Kind = SourceFileKind.Spreadsheet,
            ExtractedText = "Global ID | Item Title"
        };

        var built = NewBuilder().Build(new[] { source }, modelSupportsVision: false);
        var text = string.Concat(built.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));

        Assert.DoesNotContain("NGƯỜI DÙNG CHỐT", text);
    }

    private SourceContextBuilder NewBuilder(int? maxImagesPerCall = null)
    {
        var settings = new Dictionary<string, string?>();
        if (maxImagesPerCall.HasValue)
            settings["Llm:SourceUpload:MaxImagesPerCall"] = maxImagesPerCall.Value.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance);
    }

    private string NewSourceDir(string name, int figureCount)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        for (var n = 1; n <= figureCount; n++)
            File.WriteAllBytes(Path.Combine(dir, $"figure-{n}.png"), OnePixelPng);
        return dir;
    }

    private static ICOGenerator.Domain.ProjectSourceFile WordSource(string dir, int figureCount) => new()
    {
        Kind = SourceFileKind.Document,
        FileName = "tai-lieu.docx",
        ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        StoredPath = Path.Combine(dir, "tai-lieu.docx"),
        ExtractedText = "Quy trình nhập kho.",
        ScannedPageImageCount = figureCount,
        IsVisionSource = true,
    };

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

        Assert.Equal(3, withVision.Contents.OfType<Microsoft.Extensions.AI.DataContent>().Count());
        Assert.DoesNotContain(noVision.Contents, c => c is Microsoft.Extensions.AI.DataContent);

        // Có ảnh gửi kèm ⇒ KHÔNG được nói "nội dung bị bỏ qua" (câu đó khiến BA đi hỏi lại thứ nó đang cầm).
        var visionText = string.Concat(withVision.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));
        Assert.Contains("dưới dạng ẢNH", visionText);
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

    // Docx có một đoạn văn + một ảnh PNG lớn nhúng qua Drawing → Blip (extractor tìm Blip ở mọi độ sâu).
    // PNG chỉ cần header hợp lệ với width/height đủ lớn — extractor không decode ảnh.
    private static byte[] BuildDocxWithLargeImage(string paragraph)
    {
        var png = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        png[11] = 13;
        System.Text.Encoding.ASCII.GetBytes("IHDR").CopyTo(png, 12);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 800);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 600);

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

            var part = main.AddImagePart("image/png");
            using (var stream = new MemoryStream(png))
                part.FeedData(stream);
            body.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                new DocumentFormat.OpenXml.Wordprocessing.Run(
                    new DocumentFormat.OpenXml.Wordprocessing.Drawing(
                        new DocumentFormat.OpenXml.Drawing.Blip { Embed = main.GetIdOfPart(part) }))));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task IngestAsync_TextPdfWithFigure_ExtractsImage_AndMarksItInText()
    {
        // Trang CÓ chữ vẫn có thể chứa sơ đồ/ảnh màn hình — trước đây phần hình bị bỏ trắng vì chỉ trang
        // scan mới được lấy ảnh. Hình phải ra file figure-{n}.png VÀ có mốc [Hình n] đúng trang trong text.
        var pdf = BuildPdfWithFigures("Quy trinh duyet de nghi mua hang.", (600, 400));
        var ingestor = NewIngestor();
        using var ms = new MemoryStream(pdf);

        var entity = await ingestor.IngestAsync(
            Guid.NewGuid(), "proj-key", "quy-trinh.pdf", "application/pdf", pdf.Length, ms, "tester");

        Assert.Equal(SourceFileKind.Pdf, entity.Kind);
        Assert.Equal(1, entity.ScannedPageImageCount);
        Assert.True(entity.IsVisionSource);

        var dir = Path.GetDirectoryName(entity.StoredPath)!;
        Assert.True(File.Exists(Path.Combine(dir, "figure-1.png")));

        Assert.NotNull(entity.ExtractedText);
        Assert.Contains("--- Trang 1 ---", entity.ExtractedText);
        Assert.Contains("[Hình 1 — ảnh nhúng trong trang 1", entity.ExtractedText);
    }

    [Fact]
    public async Task IngestAsync_TextPdfWithTinyImage_SkipsDecoration()
    {
        // Logo/icon nhỏ không phải nội dung: lấy ra chỉ tổ đốt hạn mức ảnh của lượt gọi.
        var pdf = BuildPdfWithFigures("Bien ban hop giao ban dau tuan.", (60, 40));
        var ingestor = NewIngestor();
        using var ms = new MemoryStream(pdf);

        var entity = await ingestor.IngestAsync(
            Guid.NewGuid(), "proj-key", "bien-ban.pdf", "application/pdf", pdf.Length, ms, "tester");

        Assert.Equal(0, entity.ScannedPageImageCount);
        Assert.False(entity.IsVisionSource);
        Assert.DoesNotContain("[Hình", entity.ExtractedText);
    }

    [Fact]
    public async Task IngestAsync_PdfWithSameFigureOnEveryPage_ExtractsItOnce()
    {
        // Header/logo lặp trên mọi trang là ảnh giống hệt nhau: gửi 5 bản của cùng một tấm là ném hạn mức
        // ảnh đi, nên bản lặp bị loại theo nội dung bytes.
        var pdf = BuildPdfWithRepeatedFigure("Chinh sach cong tac phi.", pageCount: 3, width: 600, height: 400);
        var ingestor = NewIngestor();
        using var ms = new MemoryStream(pdf);

        var entity = await ingestor.IngestAsync(
            Guid.NewGuid(), "proj-key", "chinh-sach.pdf", "application/pdf", pdf.Length, ms, "tester");

        Assert.Equal(3, entity.PageCount);
        Assert.Equal(1, entity.ScannedPageImageCount);

        var dir = Path.GetDirectoryName(entity.StoredPath)!;
        Assert.False(File.Exists(Path.Combine(dir, "figure-2.png")));
    }

    [Fact]
    public void SourceContextBuilder_PdfWithScanPagesAndFigures_AttachesBoth_AndCountsThemTogether()
    {
        // Một PDF có thể vừa có trang bản scan vừa có hình nhúng trong trang có chữ. Câu ghi chú mang MỘT
        // con số tổng, nên nó phải đếm đủ cả hai — nói thiếu là mời model bịa phần nó tưởng không có.
        var dir = Path.Combine(_root, "mixed-pdf-src");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "page-2.png"), OnePixelPng);
        foreach (var n in new[] { 1, 2 })
            File.WriteAllBytes(Path.Combine(dir, $"figure-{n}.png"), OnePixelPng);

        var source = new ICOGenerator.Domain.ProjectSourceFile
        {
            Kind = SourceFileKind.Pdf,
            FileName = "ho-so.pdf",
            ContentType = "application/pdf",
            StoredPath = Path.Combine(dir, "ho-so.pdf"),
            ExtractedText = "--- Trang 1 ---\nMo ta quy trinh.\n[Hình 1 — ảnh nhúng trong trang 1, gửi kèm dưới dạng ảnh]",
            ScannedPageImageCount = 3,
            IsVisionSource = true,
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var builder = new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance);

        var context = builder.Build(new[] { source }, modelSupportsVision: true);

        Assert.Equal(3, context.Contents.OfType<Microsoft.Extensions.AI.DataContent>().Count());
        var text = string.Concat(context.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text));
        Assert.Contains("kèm 3 hình dưới dạng ẢNH", text);
        Assert.Contains("[Hình n]", text);
    }

    [Fact]
    public void SourceContextBuilder_SourceFileNamedLikeAnAsset_IsNotSentAsImage()
    {
        // File gốc nằm CHUNG thư mục với ảnh lấy ra, nên một file người dùng đặt tên "figure-1.pdf" lọt đúng
        // vào mẫu tìm ảnh. Gửi nguyên file PDF đi dưới media type image/png thì lời gọi model hỏng mà không
        // có gì chỉ ra vì sao.
        var dir = Path.Combine(_root, "trap-name-src");
        Directory.CreateDirectory(dir);
        var storedPath = Path.Combine(dir, "figure-1.pdf");
        File.WriteAllBytes(storedPath, new byte[] { 0x25, 0x50, 0x44, 0x46 }); // "%PDF"
        File.WriteAllBytes(Path.Combine(dir, "figure-2.png"), OnePixelPng);

        var source = new ICOGenerator.Domain.ProjectSourceFile
        {
            Kind = SourceFileKind.Pdf,
            FileName = "figure-1.pdf",
            ContentType = "application/pdf",
            StoredPath = storedPath,
            ExtractedText = "Noi dung tai lieu.",
            ScannedPageImageCount = 1,
            IsVisionSource = true,
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var builder = new SourceContextBuilder(config, NullLogger<SourceContextBuilder>.Instance);

        var images = builder.Build(new[] { source }, modelSupportsVision: true)
            .Contents.OfType<Microsoft.Extensions.AI.DataContent>().ToList();

        Assert.Single(images);
        Assert.Equal(OnePixelPng.Length, images[0].Data.Length);
    }

    // PDF một trang: có chữ (⇒ trang CÓ text, không phải trang scan) và các hình PNG với kích thước cho trước.
    private static byte[] BuildPdfWithFigures(string text, params (int Width, int Height)[] figures)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        page.AddText(text, 12, new PdfPoint(50, 780), font);

        var y = 600.0;
        foreach (var (width, height) in figures)
        {
            page.AddPng(BuildPng(width, height), new PdfRectangle(50, y, 350, y + 150));
            y -= 200;
        }
        return builder.Build();
    }

    // PDF nhiều trang, mỗi trang có chữ + ĐÚNG MỘT hình giống hệt nhau (bytes như nhau) — mô phỏng logo/header lặp.
    private static byte[] BuildPdfWithRepeatedFigure(string text, int pageCount, int width, int height)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var png = BuildPng(width, height);
        for (var i = 1; i <= pageCount; i++)
        {
            var page = builder.AddPage(595, 842);
            page.AddText($"{text} Trang {i}.", 12, new PdfPoint(50, 780), font);
            page.AddPng(png, new PdfRectangle(50, 500, 350, 650));
        }
        return builder.Build();
    }

    // PNG THẬT (PdfPig phải giải mã được để nhúng vào PDF, nên header giả như ở test Word là không đủ):
    // RGB 8-bit, không interlace, IDAT nén bằng ZLibStream có sẵn trong .NET — không kéo thêm thư viện ảnh.
    // Vẽ gradient theo cột để ảnh không phải một vùng phẳng.
    private static byte[] BuildPng(int width, int height)
    {
        var raw = new byte[height * (1 + width * 3)];
        var i = 0;
        for (var y = 0; y < height; y++)
        {
            raw[i++] = 0; // filter type None
            for (var x = 0; x < width; x++)
            {
                raw[i++] = (byte)(x % 256);
                raw[i++] = (byte)(y % 256);
                raw[i++] = 90;
            }
        }

        using var idat = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(
                   idat, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);

        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // color type: truecolour RGB
        // [10] compression, [11] filter, [12] interlace = 0

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WritePngChunk(png, "IHDR", header);
        WritePngChunk(png, "IDAT", idat.ToArray());
        WritePngChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WritePngChunk(Stream target, string type, byte[] data)
    {
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        target.Write(length);

        var typeAndData = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typeAndData, 0);
        data.CopyTo(typeAndData, 4);
        target.Write(typeAndData);

        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        target.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
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
