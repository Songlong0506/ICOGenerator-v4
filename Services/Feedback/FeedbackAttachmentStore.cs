using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Feedback;

/// <summary>
/// Lưu file đính kèm của phản hồi xuống đĩa và dựng các <see cref="FeedbackAttachment"/> (CHƯA add DB —
/// caller tự add cùng <see cref="Feedback"/>). Hỗ trợ ảnh / PDF / tài liệu Office / video. Validate theo
/// LÔ trước khi ghi bất kỳ file nào: định dạng/size/số lượng sai ⇒ ném <see cref="FeedbackValidationException"/>
/// và KHÔNG để lại file mồ côi trên đĩa. Thư mục gốc cấu hình qua <c>Feedback:UploadRootPath</c>
/// (mặc định {ContentRoot}/FeedbackUploads), mỗi phản hồi một thư mục con theo Id.
/// </summary>
public class FeedbackAttachmentStore
{
    // Map phần mở rộng → (nhóm hiển thị, MIME chuẩn). Đây cũng là DANH SÁCH ĐỊNH DẠNG ĐƯỢC PHÉP: file ngoài
    // map này bị từ chối. Đủ phủ ảnh, PDF, tài liệu Office/text và video phổ biến mà người dùng hay đính kèm.
    private static readonly IReadOnlyDictionary<string, (FeedbackAttachmentKind Kind, string ContentType)> AllowedExtensions =
        new Dictionary<string, (FeedbackAttachmentKind, string)>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = (FeedbackAttachmentKind.Image, "image/png"),
            [".jpg"] = (FeedbackAttachmentKind.Image, "image/jpeg"),
            [".jpeg"] = (FeedbackAttachmentKind.Image, "image/jpeg"),
            [".gif"] = (FeedbackAttachmentKind.Image, "image/gif"),
            [".webp"] = (FeedbackAttachmentKind.Image, "image/webp"),
            [".bmp"] = (FeedbackAttachmentKind.Image, "image/bmp"),
            [".svg"] = (FeedbackAttachmentKind.Image, "image/svg+xml"),
            [".pdf"] = (FeedbackAttachmentKind.Pdf, "application/pdf"),
            [".doc"] = (FeedbackAttachmentKind.Document, "application/msword"),
            [".docx"] = (FeedbackAttachmentKind.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            [".xls"] = (FeedbackAttachmentKind.Document, "application/vnd.ms-excel"),
            [".xlsx"] = (FeedbackAttachmentKind.Document, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            [".ppt"] = (FeedbackAttachmentKind.Document, "application/vnd.ms-powerpoint"),
            [".pptx"] = (FeedbackAttachmentKind.Document, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
            [".txt"] = (FeedbackAttachmentKind.Document, "text/plain"),
            [".csv"] = (FeedbackAttachmentKind.Document, "text/csv"),
            [".md"] = (FeedbackAttachmentKind.Document, "text/markdown"),
            [".rtf"] = (FeedbackAttachmentKind.Document, "application/rtf"),
            [".mp4"] = (FeedbackAttachmentKind.Video, "video/mp4"),
            [".webm"] = (FeedbackAttachmentKind.Video, "video/webm"),
            [".mov"] = (FeedbackAttachmentKind.Video, "video/quicktime"),
            [".avi"] = (FeedbackAttachmentKind.Video, "video/x-msvideo"),
            [".mkv"] = (FeedbackAttachmentKind.Video, "video/x-matroska"),
            [".m4v"] = (FeedbackAttachmentKind.Video, "video/x-m4v"),
        };

    private readonly string _rootPath;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private readonly ILogger<FeedbackAttachmentStore> _logger;

    public FeedbackAttachmentStore(IConfiguration configuration, IWebHostEnvironment environment, ILogger<FeedbackAttachmentStore> logger)
    {
        _logger = logger;

        var configuredRoot = configuration["Feedback:UploadRootPath"];
        _rootPath = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(environment.ContentRootPath, "FeedbackUploads")
            : Path.GetFullPath(configuredRoot);

        _maxFileBytes = configuration.GetValue("Feedback:MaxFileBytes", 50L * 1024 * 1024);
        _maxFiles = configuration.GetValue("Feedback:MaxFilesPerFeedback", 8);
    }

    public long MaxFileBytes => _maxFileBytes;
    public int MaxFiles => _maxFiles;
    public static IEnumerable<string> AllowedExtensionList => AllowedExtensions.Keys;

    /// <summary>
    /// Validate (định dạng/size/số lượng) toàn bộ lô rồi ghi xuống đĩa, trả về danh sách metadata để caller
    /// add vào DB. Lô rỗng ⇒ trả về danh sách rỗng (phản hồi không bắt buộc có file đính kèm).
    /// </summary>
    public async Task<List<FeedbackAttachment>> StoreAsync(
        Guid feedbackId, IReadOnlyList<IFormFile>? files, CancellationToken cancellationToken = default)
    {
        var valid = files?.Where(f => f is { Length: > 0 }).ToList() ?? new List<IFormFile>();
        if (valid.Count == 0)
            return new List<FeedbackAttachment>();

        if (valid.Count > _maxFiles)
            throw new FeedbackValidationException($"Tối đa {_maxFiles} file đính kèm cho mỗi phản hồi (bạn đã chọn {valid.Count}).");

        // Pha 1: validate TẤT CẢ trước khi ghi để không để lại file mồ côi nếu một file cuối lô không hợp lệ.
        foreach (var file in valid)
        {
            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(ext) || !AllowedExtensions.ContainsKey(ext))
                throw new FeedbackValidationException(
                    $"Định dạng không hỗ trợ: \"{file.FileName}\". Chỉ nhận ảnh, PDF, tài liệu (Word/Excel/PowerPoint/text) hoặc video.");
            if (file.Length > _maxFileBytes)
                throw new FeedbackValidationException($"File \"{file.FileName}\" vượt giới hạn {_maxFileBytes / 1024 / 1024}MB.");
        }

        // Pha 2: ghi từng file vào thư mục riêng của phản hồi.
        var dir = Path.Combine(_rootPath, feedbackId.ToString("N"));
        Directory.CreateDirectory(dir);

        var attachments = new List<FeedbackAttachment>(valid.Count);
        foreach (var file in valid)
        {
            var ext = Path.GetExtension(file.FileName);
            var (kind, contentType) = AllowedExtensions[ext];

            var storedPath = Path.Combine(dir, $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}");
            await using (var stream = new FileStream(storedPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            attachments.Add(new FeedbackAttachment
            {
                FeedbackId = feedbackId,
                Kind = kind,
                FileName = SanitizeDisplayName(file.FileName),
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? contentType : file.ContentType,
                SizeBytes = file.Length,
                StoredPath = storedPath,
            });
        }

        return attachments;
    }

    /// <summary>Xóa thư mục file của một phản hồi (best-effort) — gọi khi xóa phản hồi.</summary>
    public void DeleteFiles(Guid feedbackId)
    {
        var dir = Path.Combine(_rootPath, feedbackId.ToString("N"));
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không xóa được thư mục file đính kèm của phản hồi {FeedbackId}.", feedbackId);
        }
    }

    // Chỉ giữ tên file (bỏ đường dẫn) và thay ký tự không hợp lệ — dùng để HIỂN THỊ và làm tên khi tải về.
    // Tên lưu trên đĩa là Guid nên không phụ thuộc chuỗi này (tránh path traversal qua tên hiển thị).
    private static string SanitizeDisplayName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return string.IsNullOrWhiteSpace(name) ? "attachment" : name;
    }
}
