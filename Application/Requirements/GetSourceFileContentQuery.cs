using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public record SourceFileContentResult(string FilePath, string ContentType, string FileName);

/// <summary>
/// Trả đường dẫn + content type của một tài liệu nguồn (ProjectSourceFile) để controller stream file gốc
/// về trình duyệt — dùng cho ảnh đính kèm hiển thị trong bubble hội thoại. Null khi bản ghi không còn
/// (nguồn đã bị xóa) hoặc file trên đĩa đã mất — caller trả 404, bubble ẩn ảnh hỏng.
/// </summary>
public class GetSourceFileContentQuery
{
    private readonly AppDbContext _db;

    public GetSourceFileContentQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<SourceFileContentResult?> ExecuteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var source = await _db.ProjectSourceFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source == null || string.IsNullOrEmpty(source.StoredPath) || !File.Exists(source.StoredPath))
            return null;

        var contentType = string.IsNullOrWhiteSpace(source.ContentType) ? "application/octet-stream" : source.ContentType;
        return new SourceFileContentResult(source.StoredPath, contentType, source.FileName);
    }
}
