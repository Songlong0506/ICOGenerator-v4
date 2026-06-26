using System.Text;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
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
/// caller tự add + SaveChanges). Với PDF: CHỈ bóc text từng trang bằng PdfPig. PDF dạng scan/ảnh (trang gần như
/// không có text) KHÔNG được hỗ trợ — các trang đó bị bỏ qua, không OCR/không render ảnh; nếu cả file không có
/// text nào thì <see cref="ProjectSourceFile.ExtractedText"/> để null. Muốn dùng ảnh làm nguồn vision thì upload
/// trực tiếp file ảnh (PNG/JPG/WebP/GIF).
/// </summary>
public class ProjectSourceIngestor
{
    private static readonly string[] AllowedImageTypes = { "image/png", "image/jpeg", "image/jpg", "image/webp", "image/gif" };
    private static readonly string[] AllowedImageExts = { ".png", ".jpg", ".jpeg", ".webp", ".gif" };
    private const string PdfType = "application/pdf";

    // Trang PDF có ít hơn ngần này ký tự (sau trim) coi như "scan/ảnh" (không có text dùng được) ⇒ bỏ qua trang đó.
    private const int MinCharsForTextPage = 12;

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
            ProcessPdf(bytes, entity, cancellationToken);
        }

        return entity;
    }

    // Chỉ bóc text từng trang bằng PdfPig. Trang gần như không có text (PDF scan/ảnh) bị bỏ qua — app KHÔNG hỗ trợ
    // PDF chỉ-ảnh (không OCR/không render). Best-effort: PDF hỏng/khóa ⇒ giữ nguyên file gốc, bỏ qua bóc text.
    private void ProcessPdf(byte[] bytes, ProjectSourceFile entity, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

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
                if (trimmed.Length < MinCharsForTextPage)
                    continue; // trang scan/ảnh: không có text dùng được ⇒ bỏ qua.

                sb.AppendLine($"--- Trang {pageNumber} ---");
                sb.AppendLine(trimmed);
                sb.AppendLine();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không đọc được PDF {File}; giữ nguyên file gốc, bỏ qua bóc text.", entity.FileName);
        }

        // PDF không bao giờ là nguồn vision: chỉ-ảnh thì ExtractedText để null, người dùng nên upload ảnh trực tiếp.
        entity.ExtractedText = sb.Length > 0 ? sb.ToString() : null;
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
