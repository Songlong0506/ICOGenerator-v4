using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum DeleteGuestPocCommentResult
{
    Ok,

    /// <summary>Không có ghi chú đó trong link này — kể cả ghi chú của link khác (không để lộ là nó tồn tại).</summary>
    NotFound,

    /// <summary>Ghi chú đã gửi cho đội dự án (Send Note) — từ đó chỉ người tạo dự án quyết được về nó.</summary>
    AlreadySent,

    /// <summary>Bản demo đã được nghiệm thu ⇒ nội dung đang khoá (xem <see cref="PocAcceptanceGate"/>).</summary>
    PocAccepted
}

/// <summary>
/// Khách xoá một BẢN NHÁP của mình (nút Delete trên trang khách). Chỉ được khi ghi chú còn
/// <see cref="PocCommentStatus.GuestDraft"/> và thuộc đúng link đang mở: chưa ai ngoài người gõ thấy nó, nên
/// xoá THẬT (không thu hồi mềm). Đã bấm Send Note thì ghi chú thuộc về đội dự án — khách muốn rút lại thì báo
/// đội dự án, như mọi góp ý đã gửi đi.
/// </summary>
public class DeleteGuestPocCommentUseCase
{
    private readonly AppDbContext _db;
    private readonly PocAcceptanceGate _acceptanceGate;

    public DeleteGuestPocCommentUseCase(AppDbContext db, PocAcceptanceGate acceptanceGate)
    {
        _db = db;
        _acceptanceGate = acceptanceGate;
    }

    public async Task<DeleteGuestPocCommentResult> ExecuteAsync(
        PocShareAccess access, Guid commentId, CancellationToken cancellationToken = default)
    {
        var comment = await _db.PocComments.FirstOrDefaultAsync(
            x => x.Id == commentId && x.ProjectId == access.ProjectId && x.PocShareLinkId == access.LinkId,
            cancellationToken);
        if (comment == null)
            return DeleteGuestPocCommentResult.NotFound;

        if (await _acceptanceGate.IsLockedAsync(access.ProjectId, cancellationToken))
            return DeleteGuestPocCommentResult.PocAccepted;

        if (comment.Status != PocCommentStatus.GuestDraft)
            return DeleteGuestPocCommentResult.AlreadySent;

        // Bản nháp của khách nằm trong tập con trỏ harvest đếm (PocFeedbackMemoryService không lọc trạng
        // thái) — xoá thật một dòng trước con trỏ phải lùi con trỏ, như WithdrawPocCommentUseCase.
        await WithdrawPocCommentUseCase.KeepHarvestCursorAlignedAsync(_db, comment, cancellationToken);
        _db.PocComments.Remove(comment);
        await _db.SaveChangesAsync(cancellationToken);
        return DeleteGuestPocCommentResult.Ok;
    }
}
