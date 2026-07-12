using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public record BriefCommentVm(
    Guid Id,
    string AuthorUsername,
    string? AuthorDisplayName,
    string? AnchorText,
    string Content,
    DateTime CreatedAt,
    DateTime? ResolvedAt,
    string? ResolvedByUsername,
    bool CanResolve);

/// <summary>Toàn bộ góp ý Product Brief của một project (mở trước, mới trước) + số góp ý còn mở.</summary>
public record BriefCommentsVm(int OpenCount, IReadOnlyList<BriefCommentVm> Comments);

public class GetBriefCommentsQuery
{
    private readonly AppDbContext _db;

    public GetBriefCommentsQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<BriefCommentsVm?> ExecuteAsync(Guid projectId, string? actorUsername, bool canManageAll, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.CreatedByUsername })
            .FirstOrDefaultAsync(cancellationToken);

        if (project == null)
            return null;

        var rows = await _db.BriefComments.AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.AuthorUsername,
                AuthorDisplayName = _db.AppUsers.Where(u => u.Username == c.AuthorUsername).Select(u => u.DisplayName).FirstOrDefault(),
                c.AnchorText,
                c.Content,
                c.CreatedAt,
                c.ResolvedAt,
                c.ResolvedByUsername
            })
            .ToListAsync(cancellationToken);

        // Ai được resolve một góp ý: chính tác giả, chủ project, hoặc người xem-tất-cả (TeamDev/Admin).
        var isOwner = project.CreatedByUsername != null && project.CreatedByUsername == actorUsername;

        var comments = rows
            .OrderBy(c => c.ResolvedAt != null) // góp ý còn mở nổi lên trước
            .ThenByDescending(c => c.CreatedAt)
            .Select(c => new BriefCommentVm(
                c.Id, c.AuthorUsername, c.AuthorDisplayName, c.AnchorText, c.Content,
                c.CreatedAt, c.ResolvedAt, c.ResolvedByUsername,
                CanResolve: c.ResolvedAt == null && (canManageAll || isOwner || c.AuthorUsername == actorUsername)))
            .ToList();

        return new BriefCommentsVm(comments.Count(c => c.ResolvedAt == null), comments);
    }
}
