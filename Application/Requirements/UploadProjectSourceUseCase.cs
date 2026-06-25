using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum UploadProjectSourceResult { Ok, ProjectNotFound, NoFiles }

/// <summary>
/// Nhận các file (ảnh/PDF) người dùng upload làm tài liệu nguồn cho project: ingest từng file (lưu đĩa + bóc text
/// PDF + render trang scan) rồi lưu metadata vào DB. Atomic theo lô: ném <see cref="SourceFileValidationException"/>
/// nếu một file không hợp lệ và KHÔNG lưu gì (controller bắt để báo người dùng).
/// </summary>
public class UploadProjectSourceUseCase
{
    private readonly AppDbContext _db;
    private readonly ProjectSourceIngestor _ingestor;

    public UploadProjectSourceUseCase(AppDbContext db, ProjectSourceIngestor ingestor)
    {
        _db = db;
        _ingestor = ingestor;
    }

    public async Task<UploadProjectSourceResult> ExecuteAsync(
        Guid projectId, IReadOnlyList<IFormFile> files, string? uploadedByUserId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return UploadProjectSourceResult.ProjectNotFound;

        var valid = files?.Where(f => f is { Length: > 0 }).ToList() ?? new List<IFormFile>();
        if (valid.Count == 0)
            return UploadProjectSourceResult.NoFiles;

        var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
        foreach (var file in valid)
        {
            await using var stream = file.OpenReadStream();
            var entity = await _ingestor.IngestAsync(
                projectId, projectKey, file.FileName, file.ContentType, file.Length, stream, uploadedByUserId, cancellationToken);
            _db.ProjectSourceFiles.Add(entity);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return UploadProjectSourceResult.Ok;
    }
}
