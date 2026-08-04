using ICOGenerator.Services.Artifacts;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Lưu và tìm lại các ẢNH đã gửi kèm một lời gọi model, để màn Model Invocation Detail mở được đúng những
/// tấm đã đi trên dây.
///
/// Ảnh nằm trên ĐĨA trong workspace của project (<c>99_CallLogs/{logId}/image-{n}.{ext}</c>), không vào DB:
/// một lượt chat kèm 12 screenshot là ~1.3MB base64, mà <c>AgentModelCallLogs</c> là bảng BudgetGuard quét
/// trước mỗi lời gọi model. Nằm trong workspace nghĩa là ảnh theo project khi đổi tên (thư mục được move
/// nguyên khối) và chết cùng project khi workspace bị xóa — không cần cơ chế dọn riêng.
///
/// Không thể chỉ lưu ĐƯỜNG DẪN tới file gốc: ảnh chụp POC (<c>PocVisualReviewer</c>) do Playwright chụp
/// trong RAM rồi thả, không hề tồn tại trên đĩa — mà đó lại đúng là loại ảnh cần xem lại nhất khi truy vì
/// sao agent chấm một màn hình là hỏng.
///
/// Mọi thao tác đều best-effort: lưu ảnh hỏng KHÔNG được phép làm hỏng lời gọi model hay việc ghi log.
/// </summary>
public class ModelCallImageStore
{
    private const string FolderName = "99_CallLogs";
    private const string FilePrefix = "image-";

    private readonly WorkspacePathResolver _paths;
    private readonly ILogger<ModelCallImageStore> _logger;

    public ModelCallImageStore(WorkspacePathResolver paths, IConfiguration configuration, ILogger<ModelCallImageStore> logger)
    {
        _paths = paths;
        _logger = logger;
        Enabled = configuration.GetValue("Llm:CallLog:StoreRequestImages", true);
        MaxBytesPerCall = configuration.GetValue("Llm:CallLog:MaxImageBytesPerCall", 20L * 1024 * 1024);
    }

    /// <summary>Tắt bằng <c>Llm:CallLog:StoreRequestImages=false</c> — khi đó không gom bytes ngay từ đầu.</summary>
    public bool Enabled { get; }

    public long MaxBytesPerCall { get; }

    public void Save(Guid projectId, string projectName, Guid logId, IReadOnlyList<ModelCallImage> images)
    {
        if (!Enabled || images.Count == 0)
            return;

        try
        {
            var dir = DirectoryFor(projectId, projectName, logId);
            Directory.CreateDirectory(dir);
            foreach (var image in images)
                File.WriteAllBytes(Path.Combine(dir, $"{FilePrefix}{image.Index}.{image.Extension}"), image.Bytes);
        }
        catch (Exception ex)
        {
            // Đĩa đầy / quyền ghi / RootPath cấu hình sai: bỏ qua. Log vẫn có phần mô tả ảnh trong
            // RequestJson, chỉ là bấm vào không mở được tấm nào.
            _logger.LogWarning(ex, "Không lưu được ảnh của call log {LogId}; bỏ qua.", logId);
        }
    }

    /// <summary>Đường dẫn tấm ảnh thứ <paramref name="index"/> (1-based), hoặc null nếu không còn/chưa từng có.</summary>
    public string? Find(Guid projectId, string projectName, Guid logId, int index)
    {
        if (index <= 0)
            return null;

        try
        {
            var dir = DirectoryFor(projectId, projectName, logId);
            if (!Directory.Exists(dir))
                return null;
            // Đuôi file suy từ media type lúc lưu nên không đoán trước được — tìm theo tiền tố + số.
            return Directory.EnumerateFiles(dir, $"{FilePrefix}{index}.*").FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không đọc được thư mục ảnh của call log {LogId}.", logId);
            return null;
        }
    }

    // Thư mục nằm TRONG workspace project (GetProjectWorkspacePath tự chặn thoát khỏi root), tên thư mục con
    // là logId dạng "N" — thuần hex, không có gì để chèn path traversal.
    private string DirectoryFor(Guid projectId, string projectName, Guid logId) =>
        Path.Combine(
            _paths.GetProjectWorkspacePath(WorkspacePathResolver.GetWorkspaceFolder(projectId, projectName)),
            FolderName,
            logId.ToString("N"));
}
