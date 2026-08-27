using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>Một dòng lịch sử nói về cái gì — quyết định cách bảng hiển thị cột "Loại".</summary>
public enum PocNoteHistoryKind
{
    /// <summary>Ghi chú trên bản mô tả sản phẩm (Product Brief).</summary>
    BriefNote,

    /// <summary>Ghi chú ghim trên bản demo.</summary>
    PocNote,

    /// <summary>Một vòng Developer agent chỉnh bản demo — bàn giao của agent là "Repair Log" của dòng.</summary>
    Revision
}

/// <summary>
/// Một dòng của bảng lịch sử. KHÔNG có hành động nào trên dòng: bảng này chỉ đọc, và không dòng nào bị
/// xoá (ghi chú bỏ đi là <see cref="Withdrawn"/>).
/// </summary>
public record PocNoteHistoryRow(
    PocNoteHistoryKind Kind,
    string BriefVersion,
    // Nội dung người dùng gõ; với dòng Revision là tiêu đề vòng sửa.
    string Note,
    // Neo của ghi chú: đoạn Brief được trích, hoặc "màn hình · phần tử" của pin trên POC.
    string? Anchor,
    string? Route,
    string Status,
    string? CreatedBy,
    DateTime CreatedAt,
    // Đã xử lý ra sao: bàn giao (cắt gọn) của vòng sửa cạnh ghi chú, hoặc bàn giao TOÀN VĂN ở dòng Revision.
    string? RepairLog,
    DateTime? RepairedAt,
    bool Withdrawn,
    string? WithdrawnBy);

/// <summary>Các dòng của MỘT phiên bản Product Brief — bảng lịch sử gom theo version.</summary>
public record PocNoteHistoryVersion(string BriefVersion, IReadOnlyList<PocNoteHistoryRow> Rows);

/// <summary>
/// LỊCH SỬ GHI CHÚ của dự án, gom theo phiên bản Product Brief — thay cho hai panel cũ ("Nhật ký vòng
/// sửa" chỉ có bàn giao của agent, danh sách ghi chú chỉ có bản hiện hành). Ba nguồn đổ vào một bảng vì
/// người truy lại chỉ có một câu hỏi: <i>bản V{n} từng bị chê gì và ai xử lý ra sao</i>.
/// <list type="number">
///   <item>Ghi chú Brief (<see cref="PocCommentTarget.Brief"/>) — trước đây không lưu dòng nào.</item>
///   <item>Ghi chú ghim trên POC, kể cả dòng đã thu hồi.</item>
///   <item>Các vòng Developer chỉnh bản demo (<see cref="Domain.AgentTask"/> có <c>RevisionFeedback</c>).</item>
/// </list>
/// </summary>
public class GetPocNoteHistoryQuery
{
    private readonly AppDbContext _db;

    public GetPocNoteHistoryQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<PocNoteHistoryVersion>> ExecuteAsync(
        Guid projectId, CancellationToken cancellationToken = default)
    {
        var notes = await _db.PocComments.AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        var revisions = await _db.AgentTasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId
                        && t.Type == AgentTaskType.PocPreview
                        && t.RevisionFeedback != null
                        && t.Status == AgentTaskStatus.Completed)
            .OrderBy(t => t.FinishedAt ?? t.CreatedAt)
            .Select(t => new { t.Id, t.Title, t.Output, t.FinishedAt, t.CreatedAt })
            .ToListAsync(cancellationToken);

        var rows = notes.Select(c => new PocNoteHistoryRow(
            c.Target == PocCommentTarget.Brief ? PocNoteHistoryKind.BriefNote : PocNoteHistoryKind.PocNote,
            c.BriefVersion,
            c.Comment,
            Anchor(c),
            c.Route?.ToString(),
            c.Status.ToString(),
            c.CreatedByUsername,
            c.CreatedAt,
            c.AddressedNote,
            c.AddressedAtUtc,
            c.WithdrawnAtUtc.HasValue,
            c.WithdrawnByUsername)).ToList();

        // Vòng sửa đứng ở version của chính các ghi chú nó mang đi. Vòng chạy bằng nhận xét gõ tay (không
        // ghi chú nào) không suy ra được version — để trống thay vì đoán bừa, dòng vẫn xếp đúng chỗ theo
        // thời gian trong nhóm "không rõ phiên bản".
        var versionByTask = notes
            .Where(c => c.RevisionTaskId.HasValue)
            .GroupBy(c => c.RevisionTaskId!.Value)
            .ToDictionary(g => g.Key, g => g.First().BriefVersion);

        rows.AddRange(revisions.Select(t => new PocNoteHistoryRow(
            PocNoteHistoryKind.Revision,
            versionByTask.TryGetValue(t.Id, out var version) ? version : string.Empty,
            t.Title,
            Anchor: null,
            Route: PocCommentRoute.FixPoc.ToString(),
            Status: AgentTaskStatus.Completed.ToString(),
            CreatedBy: null,
            CreatedAt: t.FinishedAt ?? t.CreatedAt,
            RepairLog: t.Output,
            RepairedAt: t.FinishedAt,
            Withdrawn: false,
            WithdrawnBy: null)));

        return rows
            .GroupBy(r => r.BriefVersion)
            .Select(g => new PocNoteHistoryVersion(g.Key, g.OrderBy(r => r.CreatedAt).ToList()))
            .OrderByDescending(g => SortKey(g.BriefVersion))
            .ToList();
    }

    /// <summary>Neo hiển thị: đoạn Brief được trích, hoặc "màn hình · phần tử" của pin trên POC.</summary>
    private static string? Anchor(Domain.PocComment c)
    {
        if (c.Target == PocCommentTarget.Brief)
            return string.IsNullOrWhiteSpace(c.Quote) ? null : $"“{c.Quote}”";

        var parts = new[] { c.PageView, c.ElementLabel }.Where(x => !string.IsNullOrWhiteSpace(x));
        var anchor = string.Join(" · ", parts);
        return anchor.Length == 0 ? null : anchor;
    }

    // Bản mới nhất lên đầu: draft (bản đang soạn) > V{n} lớn nhất > … > không rõ phiên bản.
    private static int SortKey(string briefVersion)
    {
        if (briefVersion == BriefVersionResolver.DraftVersion)
            return int.MaxValue;
        if (briefVersion.StartsWith('V') && int.TryParse(briefVersion[1..], out var n))
            return n;
        return int.MinValue;
    }
}
