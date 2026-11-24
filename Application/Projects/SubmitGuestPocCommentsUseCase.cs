using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum SubmitGuestPocCommentsResult
{
    Ok,

    /// <summary>Link này không còn bản nháp nào để gửi (bấm hai lần, hoặc tab cũ).</summary>
    NothingToSend,

    /// <summary>Bản demo đã được nghiệm thu ⇒ nội dung đang khoá (xem <see cref="PocAcceptanceGate"/>).</summary>
    PocAccepted
}

/// <summary>
/// Nút "Send Note" của trang khách: chuyển MỌI bản nháp (<see cref="PocCommentStatus.GuestDraft"/>) của đúng
/// link đang mở sang <see cref="PocCommentStatus.Open"/> — từ lúc đó chúng hiện ở bảng POC Review của người
/// tạo dự án như ghi chú Draft bình thường, và người tạo dự án quyết có gửi chúng cho đội xử lý hay không
/// (DispatchPocFeedbackUseCase). Khách KHÔNG gửi thẳng cho agent: người tạo dự án vẫn đứng giữa.
/// <para>
/// Gửi theo lô chứ không từng ghi chú: khách gõ xong cả buổi rồi mới gửi, nên người tạo dự án nhận một lượt
/// góp ý trọn vẹn thay vì từng câu gõ dở, và khách còn sửa (xoá) được những gì mình gõ nhầm trước khi gửi.
/// </para>
/// </summary>
public class SubmitGuestPocCommentsUseCase
{
    private readonly AppDbContext _db;
    private readonly PocAcceptanceGate _acceptanceGate;

    public SubmitGuestPocCommentsUseCase(AppDbContext db, PocAcceptanceGate acceptanceGate)
    {
        _db = db;
        _acceptanceGate = acceptanceGate;
    }

    public async Task<(SubmitGuestPocCommentsResult Result, int SentCount)> ExecuteAsync(
        PocShareAccess access, CancellationToken cancellationToken = default)
    {
        if (await _acceptanceGate.IsLockedAsync(access.ProjectId, cancellationToken))
            return (SubmitGuestPocCommentsResult.PocAccepted, 0);

        var drafts = await _db.PocComments
            .Where(x => x.ProjectId == access.ProjectId
                        && x.PocShareLinkId == access.LinkId
                        && x.Status == PocCommentStatus.GuestDraft
                        && x.WithdrawnAtUtc == null)
            .ToListAsync(cancellationToken);
        if (drafts.Count == 0)
            return (SubmitGuestPocCommentsResult.NothingToSend, 0);

        // CreatedAt giữ nguyên lúc gõ: số # (thứ tự tạo) và con trỏ harvest đều xếp theo nó — đổi nó lúc gửi
        // là xáo thứ tự một tập mà con trỏ đã đếm.
        foreach (var draft in drafts)
            draft.Status = PocCommentStatus.Open;

        await _db.SaveChangesAsync(cancellationToken);
        return (SubmitGuestPocCommentsResult.Ok, drafts.Count);
    }
}
