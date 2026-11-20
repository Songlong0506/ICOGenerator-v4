using ICOGenerator.Localization;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;

namespace ICOGenerator.Services.Feedback;

/// <summary>
/// Lưu file đính kèm của phản hồi xuống đĩa và dựng các <see cref="FeedbackAttachment"/> (CHƯA add DB —
/// caller tự add cùng <see cref="Feedback"/>). Hỗ trợ ảnh / PDF / tài liệu Office / video. Validate theo
/// LÔ trước khi ghi bất kỳ file nào: định dạng/size/số lượng sai ⇒ ném <see cref="FeedbackValidationException"/>
/// và KHÔNG để lại file mồ côi trên đĩa. File nằm dưới <c>{AgentWorkspace:RootPath}/.feedback-uploads/{feedbackId:N}/</c>
/// (<see cref="WorkspacePathResolver.GetFeedbackUploadPath"/>) — dùng chung thư mục gốc đã bắt buộc đặt theo máy
/// thay vì một key cấu hình riêng. Trần cỡ/số file và danh sách định dạng là hằng số trong class này.
///
/// <para>
/// Thư mục gốc đọc LẠI mỗi lần ghi (resolver Scoped đọc <see cref="IConfiguration"/> lúc gọi), vì
/// <c>AgentWorkspace:RootPath</c> sửa được từ màn hình Settings khi app đang chạy. Vì cùng lý do đó,
/// <see cref="DeleteFiles"/> xóa theo <see cref="FeedbackAttachment.StoredPath"/> đã lưu (đường dẫn tuyệt đối)
/// chứ không ghép lại từ thư mục gốc hiện tại — ghép lại thì phản hồi gửi trước lần đổi gốc để lại file mồ côi.
/// </para>
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

    /// <summary>Trần cỡ MỖI file (50MB). 8 × 50MB vẫn dưới trần request 500MB của <c>FeedbackController.Submit</c>.</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>Số file tối đa mỗi phản hồi.</summary>
    public const int MaxFiles = 8;

    private readonly WorkspacePathResolver _paths;
    private readonly ILogger<FeedbackAttachmentStore> _logger;

    public FeedbackAttachmentStore(WorkspacePathResolver paths, ILogger<FeedbackAttachmentStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

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

        if (valid.Count > MaxFiles)
            throw new FeedbackValidationException(Loc.T("FeedbackAttachmentStore.StoreAsync", MaxFiles, valid.Count));

        // Pha 1: validate TẤT CẢ trước khi ghi để không để lại file mồ côi nếu một file cuối lô không hợp lệ.
        foreach (var file in valid)
        {
            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(ext) || !AllowedExtensions.ContainsKey(ext))
                throw new FeedbackValidationException(
                    Loc.T("FeedbackAttachmentStore.StoreAsync.2", file.FileName));
            if (file.Length > MaxFileBytes)
                throw new FeedbackValidationException(Loc.T("FeedbackAttachmentStore.StoreAsync.3", file.FileName, MaxFileBytes / 1024 / 1024));
        }

        // Pha 2: ghi từng file vào thư mục riêng của phản hồi.
        var dir = _paths.GetFeedbackUploadPath(feedbackId);
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

    /// <summary>
    /// Xóa thư mục file của một phản hồi (best-effort) — gọi khi xóa phản hồi. Thư mục lấy từ
    /// <paramref name="storedPaths"/> (đường dẫn đã lưu, có thể nằm dưới thư mục gốc CŨ hoặc dưới
    /// <c>{ContentRoot}/FeedbackUploads</c> của bản trước), cộng thư mục ứng với gốc hiện tại. Chỉ xóa thư mục
    /// mang đúng tên <c>{feedbackId:N}</c> — một <c>StoredPath</c> hỏng không bao giờ kéo theo thư mục khác.
    /// </summary>
    public void DeleteFiles(Guid feedbackId, IEnumerable<string> storedPaths)
    {
        var folderName = feedbackId.ToString("N");
        var dirs = storedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d) && Path.GetFileName(d) == folderName)
            .Select(d => d!)
            .ToList();

        try
        {
            dirs.Add(_paths.GetFeedbackUploadPath(feedbackId));
        }
        catch (InvalidOperationException)
        {
            // RootPath chưa cấu hình: vẫn dọn được theo StoredPath ở trên.
        }

        foreach (var dir in dirs.Distinct())
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete attachment folder {Dir} of feedback {FeedbackId}.", dir, feedbackId);
            }
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
