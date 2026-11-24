using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>
/// Bảng ghi chú của TRANG KHÁCH: chỉ ghi chú ghi qua ĐÚNG link đang mở (<c>PocComment.PocShareLinkId</c>) —
/// khách không thấy góp ý của khách khác, cũng không thấy ghi chú nội bộ; chỉ người tạo dự án (POC Review)
/// thấy tất cả. Gồm cả bản nháp chưa gửi (<see cref="PocCommentStatus.GuestDraft"/>) lẫn ghi chú đã gửi,
/// để khách theo được góp ý của mình tới đâu (Draft → Waiting → Apply, xem
/// <see cref="PocNoteDisplayStatus.ForGuest"/>).
/// <para>
/// Lọc theo link chứ không theo tên người nhận: hai link có thể cùng tên. Ghi chú khách tạo trước khi có cột
/// <c>PocShareLinkId</c> không thuộc link nào nên không hiện ở đây — chúng vẫn nằm nguyên ở POC Review.
/// </para>
/// </summary>
public class ListGuestPocCommentsQuery
{
    private readonly AppDbContext _db;
    private readonly PocAcceptanceGate _acceptanceGate;

    public ListGuestPocCommentsQuery(AppDbContext db, PocAcceptanceGate acceptanceGate)
    {
        _db = db;
        _acceptanceGate = acceptanceGate;
    }

    public async Task<List<PocCommentItem>> ExecuteAsync(PocShareAccess access, CancellationToken cancellationToken = default)
    {
        var locked = await _acceptanceGate.IsLockedAsync(access.ProjectId, cancellationToken);

        var comments = await _db.PocComments.AsNoTracking()
            .Where(x => x.ProjectId == access.ProjectId
                        && x.PocShareLinkId == access.LinkId
                        && x.Target == PocCommentTarget.Poc
                        && x.WithdrawnAtUtc == null)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        return comments.Select(x => PocCommentItem.ForGuest(x, locked)).ToList();
    }
}
