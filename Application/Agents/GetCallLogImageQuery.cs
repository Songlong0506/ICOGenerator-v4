using ICOGenerator.Data;
using ICOGenerator.Services.Llm;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Agents;

/// <summary>Một tấm ảnh đã gửi kèm lời gọi model, đã giải ra đường dẫn thật trên đĩa.</summary>
public record CallLogImageFile(string Path, string ContentType, string DownloadName);

/// <summary>
/// Giải "log nào, ảnh thứ mấy" thành file trên đĩa cho màn Model Invocation Detail.
///
/// Đường dẫn được dựng HOÀN TOÀN ở phía server từ id của log (project → workspace → thư mục theo logId →
/// image-{index}.{ext}); người gọi chỉ đưa được một Guid và một số nguyên. Đây là điều giữ cho endpoint xem
/// ảnh không thành đường đọc file tùy ý — tuyệt đối không nhận path từ query string.
/// </summary>
public class GetCallLogImageQuery
{
    private readonly AppDbContext _db;
    private readonly ModelCallImageStore _store;

    public GetCallLogImageQuery(AppDbContext db, ModelCallImageStore store)
    {
        _db = db;
        _store = store;
    }

    public async Task<CallLogImageFile?> ExecuteAsync(Guid logId, int index, CancellationToken cancellationToken = default)
    {
        var projectId = await _db.AgentModelCallLogs
            .AsNoTracking()
            .Where(x => x.Id == logId)
            .Select(x => (Guid?)x.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);
        if (projectId == null)
            return null;

        // Tên project là một phần của tên thư mục workspace (đổi tên project ⇒ thư mục được move theo), nên
        // luôn lấy tên HIỆN TẠI thay vì tên lúc ghi log.
        var projectName = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(cancellationToken);
        if (projectName == null)
            return null;

        var path = _store.Find(projectId.Value, projectName, logId, index);
        if (path == null)
            return null;

        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return new CallLogImageFile(path, ContentTypeFor(extension), $"call-log-{logId:N}-{index}{extension}");
    }

    private static string ContentTypeFor(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpeg" or ".jpg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };
}
