using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

// Một dòng diff đã phân loại cho client render ("same" | "added" | "removed").
public record DiffLineVm(string Type, string Text);

// Một lượt USER đã sinh ra thay đổi của revision này: nguyên văn điều người dùng gửi (câu chat, các ghi
// chú ghim trên bản xem trước Brief đã gộp, phản hồi POC chuyển về). Diff nói "đổi chỗ nào", cái này nói
// "vì sao đổi".
public record RevisionInputTurnVm(DateTime CreatedAt, string Message);

public record DocumentRevisionDiffVm(
    Guid RevisionId,
    string FileName,
    int RevisionNumber,
    int? PreviousRevisionNumber,
    string ChangeNote,
    DateTime CreatedAt,
    IReadOnlyList<DiffLineVm> Lines,
    IReadOnlyList<RevisionInputTurnVm> Inputs,
    bool InputsTruncated);

/// <summary>
/// Diff một revision so với revision LIỀN TRƯỚC của cùng tài liệu (revision đầu tiên diff với rỗng —
/// toàn bộ là "added"), kèm các lượt user đã dẫn tới bản này. Diff tính lúc xem bằng
/// <see cref="DocumentDiffService"/>, không lưu sẵn.
/// </summary>
public class GetDocumentRevisionDiffQuery
{
    // Trần số lượt hiển thị: revision ĐẦU TIÊN của một tài liệu không có mốc dưới nên khoảng của nó mở
    // tới đầu hội thoại — không chặn thì popup đổ nguyên buổi phỏng vấn vài chục lượt vào chỗ chỉ để
    // liếc "vì sao đổi". Cắt từ phía CŨ, giữ các lượt gần bản ghi nhất.
    private const int MaxInputTurns = 10;

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

        var (inputs, inputsTruncated) = await LoadInputTurnsAsync(revision, previous, cancellationToken);

        return new DocumentRevisionDiffVm(
            revision.Id,
            revision.ProjectDocument.FileName,
            revision.RevisionNumber,
            previous?.RevisionNumber,
            revision.ChangeNote,
            revision.CreatedAt,
            lines,
            inputs,
            inputsTruncated);
    }

    /// <summary>
    /// Các lượt user nằm giữa mốc của revision trước và mốc của revision này
    /// (<see cref="ProjectDocumentRevision.TriggerConversationId"/>) — tức đúng phần input đã sinh ra
    /// thay đổi mà diff đang hiển thị. Khoảng mở ở đầu, đóng ở cuối: lượt đã tính cho bản trước không
    /// bị kể lại ở bản sau.
    /// </summary>
    private async Task<(IReadOnlyList<RevisionInputTurnVm> Turns, bool Truncated)> LoadInputTurnsAsync(
        ProjectDocumentRevision revision, ProjectDocumentRevision? previous, CancellationToken cancellationToken)
    {
        var anchorIds = new List<Guid>(2);
        if (revision.TriggerConversationId.HasValue)
            anchorIds.Add(revision.TriggerConversationId.Value);
        if (previous?.TriggerConversationId != null)
            anchorIds.Add(previous.TriggerConversationId.Value);

        // IgnoreQueryFilters ở TOÀN BỘ đường đọc: "New Chat" lưu trữ hội thoại (ArchivedAt != null) và
        // global filter loại chúng khỏi mọi truy vấn thường — để nguyên thì mọi revision ghi trước lần
        // New Chat đột nhiên mất sạch phần "vì sao đổi", đúng lúc lịch sử có giá trị nhất.
        var anchors = anchorIds.Count == 0
            ? new Dictionary<Guid, DateTime>()
            : await _db.AgentConversations
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => anchorIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.CreatedAt, cancellationToken);

        // Mốc trỏ hụt — lượt đã bị xóa cứng ở đường retry, hoặc revision ghi từ trước khi có cột — thì
        // lùi về thời điểm ghi revision. Xấp xỉ này an toàn vì lượt user luôn đứng TRƯỚC bản ghi.
        var upper = AnchorTime(revision) ?? revision.CreatedAt;
        var lower = previous == null ? (DateTime?)null : AnchorTime(previous) ?? previous.CreatedAt;

        // Bỏ filter KHÔNG có nghĩa là lấy tất: chỉ các lượt CÒN SỐNG tại thời điểm ghi bản này mới là
        // input của nó. Lượt bị lưu trữ TRƯỚC đó thuộc buổi chat đã đóng — vòng soạn không hề đọc chúng,
        // kể ra là gán cho bản này một nguồn nó chưa từng thấy. Lượt bị lưu trữ SAU thì vẫn là input thật.
        var writtenAt = revision.CreatedAt;
        var projectId = revision.ProjectDocument.ProjectId;

        var query = _db.AgentConversations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId
                        && x.Role == "user"
                        && x.CreatedAt <= upper
                        && (x.ArchivedAt == null || x.ArchivedAt > writtenAt));

        if (lower.HasValue)
            query = query.Where(x => x.CreatedAt > lower.Value);

        // Lấy dư MỘT lượt để biết có bị cắt hay không mà không cần thêm một COUNT.
        var newestFirst = await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(MaxInputTurns + 1)
            .Select(x => new RevisionInputTurnVm(x.CreatedAt, x.Message))
            .ToListAsync(cancellationToken);

        var truncated = newestFirst.Count > MaxInputTurns;

        // Trả về theo thứ tự thời gian để đọc xuôi như hội thoại.
        var turns = newestFirst.Take(MaxInputTurns).Reverse().ToList();

        return (turns, truncated);

        DateTime? AnchorTime(ProjectDocumentRevision r) =>
            r.TriggerConversationId.HasValue && anchors.TryGetValue(r.TriggerConversationId.Value, out var at)
                ? at
                : null;
    }
}
