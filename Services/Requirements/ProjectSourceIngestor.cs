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
/// Nhận một file upload (ảnh / PDF / Word / bảng tính Excel-CSV), lưu xuống workspace project và dựng
/// <see cref="ProjectSourceFile"/> (CHƯA add DB — caller tự add + SaveChanges). Với PDF: bóc text từng
/// trang bằng PdfPig; trang KHÔNG có text (bản scan) được lấy ảnh nhúng cả trang ra PNG
/// (<see cref="PdfScanPageRenderer"/>) thay vì bỏ trắng, còn trang CÓ text thì lấy các hình nhúng đủ lớn
/// (sơ đồ, ảnh màn hình) ra <c>figure-{n}.png</c> kèm mốc <c>[Hình n]</c> trong text
/// (<see cref="PdfFigureExtractor"/>) — cùng cách xử lý với Word. Với Word (.docx): bóc đoạn văn
/// + bảng + hình nhúng đáng giá bằng <see cref="WordDocumentTextExtractor"/> — quy trình/biểu mẫu của phòng
/// ban gần như luôn nằm ở đây. Với bảng tính (.xlsx/.csv): bóc thành text có cấu trúc (tiêu đề cột + vài dòng mẫu) bằng
/// <see cref="SpreadsheetTextExtractor"/> — fidelity cao hơn hẳn ảnh chụp Excel.
/// </summary>
public class ProjectSourceIngestor
{
    private static readonly string[] AllowedImageTypes = { "image/png", "image/jpeg", "image/jpg", "image/webp", "image/gif" };
    private static readonly string[] AllowedImageExts = { ".png", ".jpg", ".jpeg", ".webp", ".gif" };
    private const string PdfType = "application/pdf";
    private const string WordType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

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
        // Word/bảng tính xét sau ảnh/PDF: một .csv có thể mang contentType text/plain, chỉ dựa vào đuôi file.
        var isWord = !isImage && !isPdf && WordDocumentTextExtractor.IsWordDocument(normalizedType, fileName);
        var isSpreadsheet = !isImage && !isPdf && !isWord && SpreadsheetTextExtractor.IsSpreadsheet(normalizedType, fileName);

        if (!isImage && !isPdf && !isWord && !isSpreadsheet)
            throw new SourceFileValidationException($"Định dạng không hỗ trợ: {fileName}. Chỉ nhận ảnh (PNG/JPG/WebP/GIF), PDF, Word (.docx) hoặc bảng tính (Excel .xlsx/.csv).");
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
            ContentType = isImage ? NormalizeImageType(normalizedType, ext)
                        : isPdf ? PdfType
                        : isWord ? WordType
                        : NormalizeSpreadsheetType(ext),
            SizeBytes = sizeBytes,
            StoredPath = storedPath,
            UploadedByUserId = uploadedByUserId,
        };

        if (isImage)
        {
            entity.Kind = SourceFileKind.Image;
            entity.IsVisionSource = true;
        }
        else if (isPdf)
        {
            entity.Kind = SourceFileKind.Pdf;
            ProcessPdf(bytes, entity, dir, cancellationToken);
        }
        else if (isWord)
        {
            entity.Kind = SourceFileKind.Document;
            // Bóc đoạn văn + bảng thành text, VÀ lấy các hình nhúng đáng giá (screenshot, sơ đồ) ra
            // figure-{n}.png cạnh file gốc cho model vision — tài liệu kỹ thuật hay nhét thông tin quan
            // trọng vào ảnh. Không đọc được ⇒ null/0 (giữ nguyên file gốc), người dùng vẫn thấy file đã đính kèm.
            var extraction = WordDocumentTextExtractor.Extract(bytes, dir);
            entity.ExtractedText = extraction.Text;
            entity.ScannedPageImageCount = extraction.ImageCount;
            entity.IsVisionSource = extraction.ImageCount > 0;
        }
        else
        {
            entity.Kind = SourceFileKind.Spreadsheet;
            // Bảng tính không bao giờ là nguồn vision: bóc thành text cấu trúc; không đọc được ⇒ để null
            // (giữ nguyên file gốc), người dùng vẫn thấy file đã đính kèm.
            entity.ExtractedText = SpreadsheetTextExtractor.Extract(bytes, fileName);
        }

        return entity;
    }

    // Bóc text từng trang bằng PdfPig, VÀ lấy phần hình ra cho model vision — hai đường khác nhau cho hai
    // loại trang rời nhau:
    //   • Trang gần như không có text là trang SCAN: lấy ảnh nhúng của cả trang ra (PdfScanPageRenderer),
    //     vì chữ nằm trong ảnh nên không còn đường nào khác.
    //   • Trang CÓ text vẫn có thể chứa sơ đồ/ảnh màn hình/biểu mẫu chụp lại: lấy các hình đủ lớn ra
    //     (PdfFigureExtractor) và cắm mốc "[Hình n]" vào đúng trang, đúng cách Word đã làm từ lâu — nếu chỉ
    //     bóc chữ thì cùng một tài liệu lưu .docx được gửi kèm hình còn xuất .pdf thì mất trắng phần đó.
    // Best-effort: PDF hỏng/khóa ⇒ giữ nguyên file gốc, bỏ qua cả text lẫn ảnh.
    private void ProcessPdf(byte[] bytes, ProjectSourceFile entity, string sourceDir, CancellationToken cancellationToken)
    {
        var textPages = new List<(int Number, string Text)>();
        var scannedPages = new List<int>();
        var figureCandidates = new List<PdfFigureExtractor.Candidate>();
        var seenFigureHashes = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<PdfFigure> figures = Array.Empty<PdfFigure>();
        var scanPageImages = 0;

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
                {
                    scannedPages.Add(pageNumber); // trang scan/ảnh: lấy ảnh cả trang ở lượt dưới.
                    continue;
                }

                textPages.Add((pageNumber, trimmed));
                PdfFigureExtractor.CollectPageFigures(page, figureCandidates, seenFigureHashes);
            }

            // Cả hai đường đều phải chạy khi document còn mở (PdfPig đọc lười từ stream gốc).
            scanPageImages = PdfScanPageRenderer.TryRenderScannedPages(doc, scannedPages, sourceDir, cancellationToken);
            figures = PdfFigureExtractor.WriteFigures(figureCandidates, sourceDir, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không đọc được PDF {File}; giữ nguyên file gốc, bỏ qua bóc text.", entity.FileName);
        }

        entity.ExtractedText = RenderPdfText(textPages, figures);
        // Một con số cho CẢ hai loại ảnh: SourceContextBuilder chỉ cần biết nguồn này đáng lẽ gửi kèm bao
        // nhiêu ảnh để nói đúng số trong câu ghi chú (tên cột giữ nguyên vì đã có dữ liệu trong DB).
        entity.ScannedPageImageCount = scanPageImages + figures.Count;
        entity.IsVisionSource = entity.ScannedPageImageCount > 0;
    }

    // Ghép text các trang, chèn mốc "[Hình n]" ngay dưới trang chứa hình đó để model ghép được hình với đoạn
    // mô tả quanh nó. Mốc tự nói ra số trang: tên file ảnh KHÔNG đi tới model (xem SourceContextBuilder), nên
    // đây là thứ duy nhất neo một tấm ảnh vào một chỗ trong tài liệu.
    private static string? RenderPdfText(
        IReadOnlyList<(int Number, string Text)> textPages, IReadOnlyList<PdfFigure> figures)
    {
        if (textPages.Count == 0)
            return null;

        var sb = new StringBuilder();
        foreach (var (number, text) in textPages)
        {
            sb.AppendLine($"--- Trang {number} ---");
            sb.AppendLine(text);
            foreach (var figure in figures.Where(f => f.PageNumber == number))
                sb.AppendLine($"[Hình {figure.Number} — ảnh nhúng trong trang {number}, gửi kèm dưới dạng ảnh]");
            sb.AppendLine();
        }

        return sb.Length > 0 ? sb.ToString() : null;
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

    private static string NormalizeSpreadsheetType(string ext) => ext switch
    {
        ".csv" => "text/csv",
        _ => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", // .xlsx/.xlsm
    };

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        name = name.Replace("..", "-");
        return string.IsNullOrWhiteSpace(name) ? "source" : name;
    }
}
