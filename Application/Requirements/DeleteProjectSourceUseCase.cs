using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// Xoá một tài liệu nguồn: dọn thư mục file trên đĩa (best-effort) rồi xoá bản ghi DB.
/// Trả về ProjectId để controller redirect lại workspace; null nếu không tìm thấy nguồn.
/// </summary>
public class DeleteProjectSourceUseCase
{
    private readonly AppDbContext _db;
    private readonly ILogger<DeleteProjectSourceUseCase> _logger;

    public DeleteProjectSourceUseCase(AppDbContext db, ILogger<DeleteProjectSourceUseCase> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Guid?> ExecuteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var source = await _db.ProjectSourceFiles.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source == null)
            return null;

        var projectId = source.ProjectId;
        TryDeleteOnDisk(source.StoredPath);

        _db.ProjectSourceFiles.Remove(source);
        await _db.SaveChangesAsync(cancellationToken);
        return projectId;
    }

    // File gốc + các ảnh trang render nằm trong thư mục con (theo Id) — xoá cả thư mục đó.
    private void TryDeleteOnDisk(string storedPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(storedPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không xoá được file nguồn trên đĩa: {Path}", storedPath);
        }
    }
}
