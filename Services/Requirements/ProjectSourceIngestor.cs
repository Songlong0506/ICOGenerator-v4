using System.Text;
using System.Text.Json;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ICOGenerator.Services.Requirements;

/// <summary>Lỗi validate tài liệu nguồn (định dạng/kích thước) — controller bắt để báo người dùng, không thành 500.</summary>
public class SourceFileValidationException : Exception
{
    public SourceFileValidationException(string message) : base(message) { }
}

/// <summary>
/// Nhận một file upload (ảnh/PDF), lưu xuống workspace project và dựng <see cref="ProjectSourceFile"/> (CHƯA add DB —
/// caller tự add + SaveChanges). Với PDF: bóc text từng trang bằng PdfPig; trang gần như không có text (PDF scan)
/// thì render trang đó thành PNG (PDFtoImage) để gửi cho model vision. Render là best-effort: native PDFium/SkiaSharp
/// thiếu trên một số môi trường ⇒ bỏ qua phần ảnh thay vì làm vỡ luồng upload.
/// </summary>
public class ProjectSourceIngestor
{
    private static readonly string[] AllowedImageTypes = { "image/png", "image/jpeg", "image/jpg", "image/webp", "image/gif" };
    private static readonly string[] AllowedImageExts = { ".png", ".jpg", ".jpeg", ".webp", ".gif" };
    private const string PdfType = "application/pdf";

    // Trang PDF có ít hơn ngần này ký tự (sau trim) coi như "scan/ảnh" ⇒ render thành ảnh cho vision.
    private const int MinCharsForTextPage = 12;
    // DPI render trang scan: đủ rõ để model đọc chữ mà không phình token quá mức.
    private const int RenderDpi = 150;
    // PDFium KHÔNG thread-safe ⇒ bọc mọi lời gọi render trong một lock toàn cục. Chỉ chạy lúc upload nên không phải hot path.
    private static readonly object PdfiumLock = new();

    private readonly IArtifactStorage _storage;
    private readonly ILogger<ProjectSourceIngestor> _logger;
    private readonly long _maxFileBytes;

    public ProjectSourceIngestor(IArtifactStorage storage, IConfiguration configuration, ILogger<ProjectSourceIngestor> logger)
    {
        _storage = storage;
        _logger = logger;
        _maxFileBytes = configuration.GetValue("Llm:SourceUpload:MaxFileBytes", 10L * 1024 * 1024);
    }

    public long MaxFileBytes => _maxFileBytes;

    public async Task<ProjectSourceFile> IngestAsync(
        Guid projectId, string projectKey, string fileName, string? contentType, long sizeBytes, Stream content,
        string? uploadedByUserId, CancellationToken cancellationToken = default)
    {
        var normalizedType = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var isImage = AllowedImageTypes.Contains(normalizedType) || AllowedImageExts.Contains(ext);
        var isPdf = normalizedType == PdfType || ext == ".pdf";

        if (!isImage && !isPdf)
            throw new SourceFileValidationException($"Định dạng không hỗ trợ: {fileName}. Chỉ nhận ảnh (PNG/JPG/WebP/GIF) hoặc PDF.");
        if (sizeBytes <= 0)
            throw new SourceFileValidationException($"File rỗng: {fileName}.");
        if (sizeBytes > _maxFileBytes)
            throw new SourceFileValidationException($"File \"{fileName}\" vượt giới hạn {_maxFileBytes / 1024 / 1024}MB.");

        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken);
        var bytes = ms.ToArray();

        var id = Guid.NewGuid();
        // Mỗi nguồn một thư mục con (theo Id) để chứa file gốc + các ảnh trang render, tránh đụng tên file.
        var dir = Path.Combine(_storage.GetSourceUploadDir(projectKey), id.ToString("N"));
        Directory.CreateDirectory(dir);
        var storedPath = Path.Combine(dir, SanitizeFileName(fileName));
        await File.WriteAllBytesAsync(storedPath, bytes, cancellationToken);

        var entity = new ProjectSourceFile
        {
            Id = id,
            ProjectId = projectId,
            FileName = fileName,
            ContentType = isImage ? NormalizeImageType(normalizedType, ext) : PdfType,
            SizeBytes = sizeBytes,
            StoredPath = storedPath,
            UploadedByUserId = uploadedByUserId,
        };

        if (isImage)
        {
            entity.Kind = SourceFileKind.Image;
            entity.IsVisionSource = true;
        }
        else
        {
            entity.Kind = SourceFileKind.Pdf;
            ProcessPdf(bytes, dir, entity, cancellationToken);
        }

        return entity;
    }

    // Bóc text từng trang; trang scan (gần như không text) thì render PNG để gửi vision. Toàn bộ best-effort:
    // PDF hỏng/khóa ⇒ giữ nguyên file gốc, bỏ qua bóc text (entity vẫn hợp lệ, chỉ thiếu phần văn bản).
    private void ProcessPdf(byte[] bytes, string dir, ProjectSourceFile entity, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var pageImagePaths = new List<string>();

        try
        {
            using var doc = PdfDocument.Open(bytes);
            entity.PageCount = doc.NumberOfPages;

            var pageNumber = 0;
            foreach (var page in doc.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                pageNumber++;

                string text;
                try { text = ContentOrderTextExtractor.GetText(page, true); }
                catch { text = page.Text; }

                var trimmed = (text ?? string.Empty).Trim();
                if (trimmed.Length >= MinCharsForTextPage)
                {
                    sb.AppendLine($"--- Trang {pageNumber} ---");
                    sb.AppendLine(trimmed);
                    sb.AppendLine();
                }
                else
                {
                    var pngPath = TryRenderPage(bytes, dir, pageNumber);
                    if (pngPath != null)
                        pageImagePaths.Add(pngPath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không đọc được PDF {File}; giữ nguyên file gốc, bỏ qua bóc text/render.", entity.FileName);
        }

        entity.ExtractedText = sb.Length > 0 ? sb.ToString() : null;
        if (pageImagePaths.Count > 0)
        {
            entity.PageImagePaths = JsonSerializer.Serialize(pageImagePaths);
            entity.IsVisionSource = true;
        }
    }

    private string? TryRenderPage(byte[] pdfBytes, string dir, int pageNumber1Based)
    {
        var pngPath = Path.Combine(dir, $"page-{pageNumber1Based}.png");
        try
        {
            // PDFium không thread-safe — nối tiếp mọi lời gọi render. Index 0-based.
            // CA1416: SavePng khai báo [SupportedOSPlatform] cho Windows/Linux/macOS… — phủ hết nền app này chạy
            // (Windows server + Linux dev/CI); lỗi native (nếu thiếu lib) đã được try/catch bên dưới nuốt an toàn.
            lock (PdfiumLock)
            {
                using var fs = File.Create(pngPath);
#pragma warning disable CA1416
                Conversion.SavePng(fs, pdfBytes, new Index(pageNumber1Based - 1), password: null, options: new RenderOptions(Dpi: RenderDpi));
#pragma warning restore CA1416
            }
            return pngPath;
        }
        catch (Exception ex)
        {
            // Native PDFium/SkiaSharp có thể thiếu (vd Linux headless). Đừng làm vỡ upload: mất phần ảnh của trang scan này.
            _logger.LogWarning(ex, "Render trang {Page} PDF sang ảnh thất bại (thiếu native PDFium/SkiaSharp?); bỏ qua trang.", pageNumber1Based);
            TryDelete(pngPath);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private static string NormalizeImageType(string contentType, string ext)
    {
        if (AllowedImageTypes.Contains(contentType) && contentType != "image/jpg")
            return contentType;
        return ext switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg", // .jpg/.jpeg và "image/jpg" → chuẩn hoá về image/jpeg
        };
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        name = name.Replace("..", "-");
        return string.IsNullOrWhiteSpace(name) ? "source" : name;
    }
}
