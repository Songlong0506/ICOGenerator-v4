using System.IO.Compression;
using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Prompts;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public record ReviewPackageExport(string FileName, byte[] Content);

/// <summary>
/// Phần của gói mà NGƯỜI TẢI được phép đọc. Trang Requirements cố tình không hiển thị bản kỹ thuật
/// (AI Design Spec thuộc phía Agent Dashboard), nên một nút tải về ở đây không được phép âm thầm nới
/// quyền đó — gói CO LẠI theo quyền và <c>00-README.md</c> nói rõ phần nào vắng mặt vì lý do gì.
/// </summary>
public record ReviewPackageAccess(bool CanReadDesignSpec, bool CanReadPoc)
{
    /// <summary>Đủ quyền đọc mọi phần — dùng cho test và cho các đường gọi đã tự kiểm quyền.</summary>
    public static readonly ReviewPackageAccess Full = new(true, true);
}

/// <summary>
/// Xuất CẢ CHUỖI DẪN XUẤT của một dự án (hội thoại BA → Product Brief → AI Design Spec → POC demo) thành
/// một file .zip, để người dùng đem sang một công cụ AI khác nhờ soi các mối nối giữa bốn tầng.
///
/// <para>
/// Vì sao không phải chỉ là bản xuất hội thoại có thêm phụ lục: từng tầng đã có cổng kiểm riêng
/// (<c>PocAudit</c> đối chiếu POC với spec, <c>ProductBriefReviewParser</c> soi bản mô tả), nhưng KHÔNG
/// cổng nào đặt cả bốn tầng cạnh nhau — mà lớp lỗi đắt nhất của dây chuyền này lại nằm đúng ở đó: điều
/// người dùng nói bị bản mô tả bỏ, bản mô tả bị bản kỹ thuật diễn dịch lệch, bản kỹ thuật bị POC hiện thực
/// thiếu. Mỗi tầng nhìn riêng đều "đạt".
/// </para>
///
/// <para>Chỉ ĐỌC: không đụng DB, không đụng workspace. Xem <see cref="ReviewPackageBuilder"/> về bố cục gói.</para>
/// </summary>
public class ExportReviewPackageQuery
{
    /// <summary>Chỉ dẫn cho AI đọc gói. Là file prompt nên sửa được ở Prompt Studio, không cần deploy.</summary>
    private const string ReviewBriefKey = "Eval/delivery-review.v1.md";

    private readonly AppDbContext _db;
    private readonly ExportChatTranscriptQuery _chatExport;
    private readonly PromptTemplateService _prompts;
    private readonly IProjectArtifactCatalog _artifacts;
    private readonly WorkspacePathResolver _workspace;
    private readonly ILogger<ExportReviewPackageQuery> _logger;

    public ExportReviewPackageQuery(
        AppDbContext db,
        ExportChatTranscriptQuery chatExport,
        PromptTemplateService prompts,
        IProjectArtifactCatalog artifacts,
        WorkspacePathResolver workspace,
        ILogger<ExportReviewPackageQuery> logger)
    {
        _db = db;
        _chatExport = chatExport;
        _prompts = prompts;
        _artifacts = artifacts;
        _workspace = workspace;
        _logger = logger;
    }

    /// <param name="versionName">Phiên bản Product Brief đang chọn trên màn hình (draft / V1 / V2…).</param>
    public async Task<ReviewPackageExport?> ExecuteAsync(
        Guid projectId,
        string versionName,
        ReviewPackageAccess access,
        CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects
            .AsNoTracking()
            .Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);

        if (project == null)
            return null;

        // Bản xuất hội thoại đi qua đúng use case đang phục vụ nút cũ — một chỗ duy nhất quyết định buổi
        // phỏng vấn được kể lại thế nào, nên hai đường tải về không bao giờ nói hai câu chuyện khác nhau.
        var chat = await _chatExport.ExecuteAsync(projectId, cancellationToken);
        if (chat == null)
            return null;

        var (poc, pocHtml) = access.CanReadPoc
            ? ReadPoc(project)
            : (new ReviewPackagePoc(ReviewPackagePartState.NotPermitted), null);

        var snapshot = new ReviewPackageSnapshot(
            project,
            _prompts.Get(ReviewBriefKey),
            chat.Content,
            PickDocument(project, _artifacts.ProductBrief.FileName, versionName),
            access.CanReadDesignSpec
                ? PickDocument(project, _artifacts.AiDesignSpec.FileName, versionName)
                : new ReviewPackageDoc(ReviewPackagePartState.NotPermitted),
            poc,
            DateTime.UtcNow);

        return new ReviewPackageExport(
            ExportFileName.Build("ra-soat", project.Name, snapshot.ExportedAtUtc, ".zip"),
            BuildArchive(ReviewPackageBuilder.Build(snapshot), pocHtml));
    }

    /// <summary>
    /// Bản của tài liệu được đem xuất: ưu tiên ĐÚNG phiên bản đang chọn trên màn hình, không có thì lấy bản
    /// mới nhất của bất kỳ phiên bản nào. Lý do có đường lui: AI Design Spec chỉ sinh ở lúc duyệt nên nó
    /// không bao giờ tồn tại ở <c>draft</c> — cứng nhắc theo phiên bản đang chọn thì mọi gói xuất từ màn
    /// hình đang xem bản nháp đều mất tầng kỹ thuật. Phiên bản thực tế luôn được ghi vào README, nên đường
    /// lui này không bao giờ im lặng.
    /// </summary>
    private static ReviewPackageDoc PickDocument(Project project, string fileName, string versionName)
    {
        var candidates = project.Documents
            .Where(x => x.FileName == fileName)
            .ToList();

        var document = candidates
                .Where(x => string.Equals(x.VersionName, versionName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefault()
            ?? candidates
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefault();

        if (document == null || string.IsNullOrWhiteSpace(document.Content))
            return new ReviewPackageDoc(ReviewPackagePartState.NotGenerated);

        return new ReviewPackageDoc(
            ReviewPackagePartState.Present,
            document.Content,
            document.VersionName,
            document.CreatedAt);
    }

    // Bản demo HIỆN TẠI (không phải bản chụp của một vòng cũ): đó là thứ người dùng đang xem ở POC Review
    // và cũng là thứ họ muốn đem đi hỏi. Số vòng dựng được nêu trong README để người chấm biết bản này đã
    // qua mấy lượt ghi đè.
    private (ReviewPackagePoc Info, string? Html) ReadPoc(Project project)
    {
        try
        {
            var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
            var path = _workspace.GetMockupPath(projectKey);
            if (!File.Exists(path))
                return (new ReviewPackagePoc(ReviewPackagePartState.NotGenerated), null);

            // Cùng phép gỡ với đường phục vụ POC (ProjectsController.Mockup): khối chú thích mở đầu là CHỈ
            // DẪN cho agent lập trình, không phải nội dung trang. Ở đây nó còn đáng gỡ hơn — để nguyên là
            // đưa một tập mệnh lệnh lạ vào giữa dữ liệu mà công cụ AI kia sắp đọc.
            var html = PocTemplate.StripDeveloperGuide(File.ReadAllText(path));

            var info = new ReviewPackagePoc(
                ReviewPackagePartState.Present,
                Encoding.UTF8.GetByteCount(html),
                File.GetLastWriteTimeUtc(path),
                PocSnapshots.List(_workspace.GetProjectWorkspacePath(projectKey)).Count);

            return (info, html);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Workspace chưa cấu hình / đĩa lỗi ⇒ gói vẫn tải được, chỉ thiếu bản demo. Bỏ cả gói vì một
            // file là đánh đổi tệ: ba tầng còn lại vẫn đủ để soi hai mối nối đầu.
            _logger.LogWarning(ex, "Không đọc được POC demo cho gói rà soát của project {ProjectId}", project.Id);
            return (new ReviewPackagePoc(ReviewPackagePartState.NotGenerated), null);
        }
    }

    private static byte[] BuildArchive(IReadOnlyList<ReviewPackageFile> files, string? pocHtml)
    {
        using var buffer = new MemoryStream();

        // Đóng archive TRƯỚC khi lấy mảng byte: ZipArchive chỉ ghi central directory lúc Dispose, lấy sớm
        // thì ra một file zip cụt mà mọi trình giải nén đều từ chối.
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
                WriteEntry(archive, file.Name, file.Content);

            if (pocHtml != null)
                WriteEntry(archive, ReviewPackageBuilder.PocEntry, pocHtml);
        }

        return buffer.ToArray();
    }

    // UTF-8 BOM: gói chở tiếng Việt và người dùng hay mở lại bằng Notepad trước khi gửi đi.
    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        var bytes = new UTF8Encoding(true).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
