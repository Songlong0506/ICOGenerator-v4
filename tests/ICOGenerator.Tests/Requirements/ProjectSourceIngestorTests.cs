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
        var storage = new LocalArtifactStorage(new WorkspacePathResolver(config));
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
        // Trang có text ⇒ KHÔNG render ảnh.
        Assert.Null(entity.PageImagePaths);
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
            new LocalArtifactStorage(new WorkspacePathResolver(config)), config, NullLogger<ProjectSourceIngestor>.Instance);
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
