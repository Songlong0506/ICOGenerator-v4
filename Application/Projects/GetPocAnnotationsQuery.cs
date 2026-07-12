using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public record PocAnnotationVm(
    Guid Id,
    string AuthorUsername,
    string? AuthorDisplayName,
    string ElementLabel,
    string? Comment,
    string Status,
    DateTime CreatedAt,
    bool CanDelete);

/// <summary>Danh sách annotation POC của một project + số mục còn mở/đã gửi (cho nút hành động).</summary>
public record PocAnnotationsVm(int OpenCount, int SubmittedCount, IReadOnlyList<PocAnnotationVm> Annotations);

public class GetPocAnnotationsQuery
{
    private readonly AppDbContext _db;

    public GetPocAnnotationsQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PocAnnotationsVm?> ExecuteAsync(Guid projectId, string? actorUsername, CancellationToken cancellationToken = default)
    {
        var projectExists = await _db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken);
        if (!projectExists)
            return null;

        var rows = await _db.PocAnnotations.AsNoTracking()
            .Where(a => a.ProjectId == projectId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new
            {
                a.Id,
                a.AuthorUsername,
                AuthorDisplayName = _db.AppUsers.Where(u => u.Username == a.AuthorUsername).Select(u => u.DisplayName).FirstOrDefault(),
                a.ElementLabel,
                a.Comment,
                a.Status,
                a.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var annotations = rows
            .Select(a => new PocAnnotationVm(
                a.Id, a.AuthorUsername, a.AuthorDisplayName, a.ElementLabel, a.Comment,
                a.Status.ToString(), a.CreatedAt,
                // Chỉ tác giả xóa được, và chỉ khi chưa gửi đi đâu (còn Open).
                CanDelete: a.Status == PocAnnotationStatus.Open && a.AuthorUsername == actorUsername))
            .ToList();

        return new PocAnnotationsVm(
            rows.Count(a => a.Status == PocAnnotationStatus.Open),
            rows.Count(a => a.Status == PocAnnotationStatus.Submitted),
            annotations);
    }
}
