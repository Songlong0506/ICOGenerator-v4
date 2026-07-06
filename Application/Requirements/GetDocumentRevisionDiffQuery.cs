using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

// Một dòng diff đã phân loại cho client render ("same" | "added" | "removed").
public record DiffLineVm(string Type, string Text);

public record DocumentRevisionDiffVm(
    Guid RevisionId,
    string FileName,
    int RevisionNumber,
    int? PreviousRevisionNumber,
    string ChangeNote,
    DateTime CreatedAt,
    IReadOnlyList<DiffLineVm> Lines);

/// <summary>
/// Diff một revision so với revision LIỀN TRƯỚC của cùng tài liệu (revision đầu tiên diff với rỗng —
/// toàn bộ là "added"). Diff tính lúc xem bằng <see cref="DocumentDiffService"/>, không lưu sẵn.
/// </summary>
public class GetDocumentRevisionDiffQuery
{
    private readonly AppDbContext _db;
    private readonly DocumentDiffService _diff;

    public GetDocumentRevisionDiffQuery(AppDbContext db, DocumentDiffService diff)
    {
        _db = db;
        _diff = diff;
    }

    public async Task<DocumentRevisionDiffVm?> ExecuteAsync(Guid revisionId, CancellationToken cancellationToken = default)
    {
        var revision = await _db.ProjectDocumentRevisions
            .AsNoTracking()
            .Include(x => x.ProjectDocument)
            .FirstOrDefaultAsync(x => x.Id == revisionId, cancellationToken);

        if (revision == null)
            return null;

        var previous = await _db.ProjectDocumentRevisions
            .AsNoTracking()
            .Where(x => x.ProjectDocumentId == revision.ProjectDocumentId && x.RevisionNumber < revision.RevisionNumber)
            .OrderByDescending(x => x.RevisionNumber)
            .FirstOrDefaultAsync(cancellationToken);

        var lines = _diff.Diff(previous?.Content, revision.Content)
            .Select(x => new DiffLineVm(x.Kind switch
            {
                DiffLineKind.Added => "added",
                DiffLineKind.Removed => "removed",
                _ => "same"
            }, x.Text))
            .ToList();

        return new DocumentRevisionDiffVm(
            revision.Id,
            revision.ProjectDocument.FileName,
            revision.RevisionNumber,
            previous?.RevisionNumber,
            revision.ChangeNote,
            revision.CreatedAt,
            lines);
    }
}
