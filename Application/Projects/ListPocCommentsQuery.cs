using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>
/// Một dòng của bảng ghi chú (POC Review / trang khách), ở dạng client render được. Quyền và luật trạng
/// thái tính sẵn phía server (<see cref="CanWithdraw"/>, <see cref="DisplayStatus"/>) để JS không phải đoán
/// — và để hai trang dùng chung một bảng không thể hiểu cùng một ghi chú theo hai cách.
/// </summary>
public record PocCommentItem(
    Guid Id,
    // "Poc" = ghim/ghi trên bản demo, "Brief" = ghi trên bản xem trước Product Brief (chỉ trang review).
    string Target,
    string PageView,
    string ElementLabel,
    string ElementPath,
    double XPercent,
    double YPercent,
    // Đoạn Brief được bôi đen khi ghi chú (chỉ ghi chú Brief; rỗng = chê cả bản).
    string? Quote,
    string Comment,
    // Trạng thái thật (enum) — tooltip ô Status nói chi tiết đường đi từ giá trị này.
    string Status,
    // Draft / Waiting / Apply — xem PocNoteDisplayStatus.
    string DisplayStatus,
    string? CreatedBy,
    DateTime CreatedAt,
    // Người xem rút được ghi chú này (chủ ghi chú hoặc DeliveryAdvance) VÀ ghi chú còn rút được.
    bool CanWithdraw,
    // Bản Product Brief mà ghi chú nói về ("V2").
    string BriefVersion,
    // Đường đã gửi đi ("FixPoc"/"Requirement"), null = chưa gửi.
    string? Route,
    // Ghi chú này đã từng được gửi đi chưa — quyết định nút 🗑 xoá thật hay chỉ thu hồi mềm, nên client
    // phải hỏi người dùng bằng đúng lời cảnh báo của ca đó. Xem WithdrawPocCommentUseCase.WasDispatched.
    bool WasDispatched)
{
    public static PocCommentItem From(PocComment c, bool canManage, string? currentUsername) => new(
        c.Id,
        c.Target.ToString(),
        c.PageView,
        c.ElementLabel,
        c.ElementPath,
        c.XPercent,
        c.YPercent,
        c.Quote,
        c.Comment,
        c.Status.ToString(),
        PocNoteDisplayStatus.Of(c),
        c.CreatedByUsername,
        c.CreatedAt,
        PocNoteDisplayStatus.IsWithdrawable(c)
            && (canManage || (currentUsername != null && c.CreatedByUsername == currentUsername)),
        c.BriefVersion,
        c.Route?.ToString(),
        WithdrawPocCommentUseCase.WasDispatched(c));

    /// <summary>
    /// Dòng của TRANG KHÁCH: trạng thái chiếu theo góc nhìn khách (<see cref="PocNoteDisplayStatus.ForGuest"/>),
    /// và khách chỉ xoá được bản nháp chưa gửi của chính mình — người gọi đã lọc đúng link, nên ở đây chỉ còn
    /// xét trạng thái. Xem DeleteGuestPocCommentUseCase.
    /// </summary>
    public static PocCommentItem ForGuest(PocComment c, bool locked) => From(c, canManage: false, currentUsername: null) with
    {
        DisplayStatus = PocNoteDisplayStatus.ForGuest(c),
        CanWithdraw = !locked && c.Status == PocCommentStatus.GuestDraft && c.WithdrawnAtUtc == null
    };
}

public class ListPocCommentsQuery
{
    private readonly AppDbContext _db;

    public ListPocCommentsQuery(AppDbContext db)
    {
        _db = db;
    }

    /// <param name="currentUsername">User đang xem — quyết định CanWithdraw cho ghi chú của chính họ.</param>
    /// <param name="canManage">True khi user có DeliveryAdvance (rút được mọi ghi chú).</param>
    /// <param name="includeBriefNotes">
    /// Trang review: true — bảng thay cho cả tab Lịch sử cũ, nên ghi chú trên Product Brief cũng là một dòng
    /// của nó. (Trang khách không dùng query này — xem <see cref="ListGuestPocCommentsQuery"/>.)
    /// </param>
    public async Task<List<PocCommentItem>> ExecuteAsync(
        Guid projectId, string? currentUsername, bool canManage, bool includeBriefNotes = false,
        CancellationToken cancellationToken = default)
    {
        // Dòng đã thu hồi mềm vẫn còn nguyên trong DB (vòng sửa đã chạy theo nó — RevisionTaskId trỏ vào
        // nó), nhưng người dùng đã bấm xoá nên nó không còn là một dòng của bảng. Bản nháp của khách
        // (GuestDraft) chưa được gửi cho người tạo dự án ⇒ chưa phải một dòng của bảng này.
        var comments = await _db.PocComments.AsNoTracking()
            .Where(x => x.ProjectId == projectId
                        && (includeBriefNotes || x.Target == PocCommentTarget.Poc)
                        && x.Status != PocCommentStatus.GuestDraft
                        && x.WithdrawnAtUtc == null)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        return comments.Select(x => PocCommentItem.From(x, canManage, currentUsername)).ToList();
    }
}
