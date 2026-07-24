using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public enum UploadProjectSourceStatus { Ok, ProjectNotFound, NoFiles }

/// <summary>
/// Kết quả upload tài liệu nguồn: trạng thái + tên các file đã nhận + tên các PDF là bản SCAN (không bóc
/// được text nên nội dung bị bỏ qua) — controller dùng danh sách scan để cảnh báo rõ cho người dùng thay
/// vì im lặng nuốt mất tài liệu họ tưởng đã được đọc.
/// </summary>
public record UploadProjectSourceOutcome(
    UploadProjectSourceStatus Status,
    IReadOnlyList<string> IngestedFileNames,
    IReadOnlyList<string> ScannedPdfNames,
    IReadOnlyList<ProjectSourceFile> IngestedFiles);

/// <summary>
/// Nhận các file (ảnh/PDF) người dùng upload làm tài liệu nguồn cho project: ingest từng file (lưu đĩa + bóc text
/// PDF) rồi lưu metadata vào DB. Atomic theo lô: ném <see cref="SourceFileValidationException"/>
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

    public async Task<UploadProjectSourceOutcome> ExecuteAsync(
        Guid projectId, IReadOnlyList<IFormFile> files, string? uploadedByUserId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return new UploadProjectSourceOutcome(UploadProjectSourceStatus.ProjectNotFound, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ProjectSourceFile>());

        var valid = files?.Where(f => f is { Length: > 0 }).ToList() ?? new List<IFormFile>();
        if (valid.Count == 0)
            return new UploadProjectSourceOutcome(UploadProjectSourceStatus.NoFiles, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ProjectSourceFile>());

        var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
        var ingested = new List<ProjectSourceFile>();
        var scanned = new List<string>();
        foreach (var file in valid)
        {
            await using var stream = file.OpenReadStream();
            var entity = await _ingestor.IngestAsync(
                projectId, projectKey, file.FileName, file.ContentType, file.Length, stream, uploadedByUserId, cancellationToken);
            _db.ProjectSourceFiles.Add(entity);
            ingested.Add(entity);

            // PDF không bóc được text nào = bản scan/ảnh: nội dung sẽ KHÔNG được BA đọc (app không OCR).
            // Gom lại để cảnh báo người dùng, tránh cảm giác "đã tải lên rồi mà BA không thấy gì".
            if (entity.Kind == SourceFileKind.Pdf && string.IsNullOrWhiteSpace(entity.ExtractedText))
                scanned.Add(entity.FileName);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return new UploadProjectSourceOutcome(UploadProjectSourceStatus.Ok, ingested.Select(x => x.FileName).ToList(), scanned, ingested);
    }
}
