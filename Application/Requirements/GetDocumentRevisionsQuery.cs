using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

// Một dòng lịch sử của tài liệu (metadata — nội dung/diff lấy riêng qua GetDocumentRevisionDiffQuery).
public record DocumentRevisionItemVm(Guid Id, int RevisionNumber, string ChangeNote, string VersionName, DateTime CreatedAt);

public record DocumentRevisionListVm(Guid DocumentId, string FileName, IReadOnlyList<DocumentRevisionItemVm> Revisions);

/// <summary>Liệt kê lịch sử revision của một tài liệu sinh ra, mới nhất trước, cho modal "Lịch sử".</summary>
public class GetDocumentRevisionsQuery
{
    private readonly AppDbContext _db;

    public GetDocumentRevisionsQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<DocumentRevisionListVm?> ExecuteAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var doc = await _db.ProjectDocuments
            .AsNoTracking()
            .Where(x => x.Id == documentId)
            .Select(x => new { x.Id, x.FileName })
            .FirstOrDefaultAsync(cancellationToken);

        if (doc == null)
            return null;

        var revisions = await _db.ProjectDocumentRevisions
            .AsNoTracking()
            .Where(x => x.ProjectDocumentId == documentId)
            .OrderByDescending(x => x.RevisionNumber)
            .Select(x => new DocumentRevisionItemVm(x.Id, x.RevisionNumber, x.ChangeNote, x.VersionName, x.CreatedAt))
            .ToListAsync(cancellationToken);

        return new DocumentRevisionListVm(doc.Id, doc.FileName, revisions);
    }
}
