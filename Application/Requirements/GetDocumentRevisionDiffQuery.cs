using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

// Một dòng diff đã phân loại cho client render ("same" | "added" | "removed").
public record DiffLineVm(string Type, string Text);

// Một ghi chú người dùng đã ghim lên bản xem trước Product Brief khi revision này đang là bản hiện hành.
// Quote rỗng = ghi chú chung cho cả bản mô tả.
public record RevisionNoteVm(DateTime CreatedAt, string? Quote, string Comment, string? CreatedByUsername);

public record DocumentRevisionDiffVm(
    Guid RevisionId,
    string FileName,
    int RevisionNumber,
    int? PreviousRevisionNumber,
    string ChangeNote,
    DateTime CreatedAt,
    IReadOnlyList<DiffLineVm> Lines,
    IReadOnlyList<RevisionNoteVm> Notes);

/// <summary>
/// Diff một revision so với revision LIỀN TRƯỚC của cùng tài liệu (revision đầu tiên diff với rỗng —
/// toàn bộ là "added"), kèm các ghi chú người dùng đã ghim lên CHÍNH bản này. Diff tính lúc xem bằng
/// <see cref="DocumentDiffService"/>, không lưu sẵn.
/// </summary>
public class GetDocumentRevisionDiffQuery
{
    private readonly AppDbContext _db;
    private readonly DocumentDiffService _diff;
    private readonly IProjectArtifactCatalog _artifactCatalog;

    public GetDocumentRevisionDiffQuery(AppDbContext db, DocumentDiffService diff, IProjectArtifactCatalog artifactCatalog)
    {
        _db = db;
        _diff = diff;
        _artifactCatalog = artifactCatalog;
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

        var notes = await LoadNotesAsync(revision, cancellationToken);

        return new DocumentRevisionDiffVm(
            revision.Id,
            revision.ProjectDocument.FileName,
            revision.RevisionNumber,
            previous?.RevisionNumber,
            revision.ChangeNote,
            revision.CreatedAt,
            lines,
            notes);
    }

    /// <summary>
    /// Ghi chú Brief người dùng đã ghim TRÊN bản này — không phải mọi điều họ từng nhắn trong khung chat.
    /// Ghi chú chỉ ghim được lên bản draft đang hiện hành, và gửi đi là khởi động vòng soạn ra revision
    /// kế tiếp, nên một ghi chú thuộc về revision nào thì khoanh bằng thời gian:
    /// <c>[revision này, revision kế của cùng document)</c>. Thêm điều kiện <see cref="Domain.PocComment.BriefVersion"/>
    /// trùng version hiện tại của document (ApproveRequirementUseCase đổi draft → V{n} cho file và ghi chú
    /// CÙNG LÚC) để revision cuối của V1 — khoảng mở ở đầu trên — không nhặt nhầm ghi chú của bản draft
    /// V2 sinh sau. Ghi chú đã thu hồi không hiện. Chỉ Product Brief có ghi chú ghim; tài liệu khác ⇒ rỗng.
    /// </summary>
    private async Task<IReadOnlyList<RevisionNoteVm>> LoadNotesAsync(
        Domain.ProjectDocumentRevision revision, CancellationToken cancellationToken)
    {
        var doc = revision.ProjectDocument;
        if (!string.Equals(doc.FileName, _artifactCatalog.ProductBrief.FileName, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<RevisionNoteVm>();

        var next = await _db.ProjectDocumentRevisions
            .AsNoTracking()
            .Where(x => x.ProjectDocumentId == revision.ProjectDocumentId && x.RevisionNumber > revision.RevisionNumber)
            .OrderBy(x => x.RevisionNumber)
            .Select(x => (DateTime?)x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var from = revision.CreatedAt;
        var query = _db.PocComments
            .AsNoTracking()
            .Where(x => x.ProjectId == doc.ProjectId
                        && x.Target == PocCommentTarget.Brief
                        && x.BriefVersion == doc.VersionName
                        && x.WithdrawnAtUtc == null
                        && x.CreatedAt >= from);

        if (next.HasValue)
            query = query.Where(x => x.CreatedAt < next.Value);

        return await query
            .OrderBy(x => x.CreatedAt)
            .Select(x => new RevisionNoteVm(x.CreatedAt, x.Quote, x.Comment, x.CreatedByUsername))
            .ToListAsync(cancellationToken);
    }
}
