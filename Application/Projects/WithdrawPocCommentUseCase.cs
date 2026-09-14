using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum WithdrawPocCommentResult
{
    Ok,

    /// <summary>Không có ghi chú đó, hoặc người gọi không phải chủ ghi chú và cũng không có DeliveryAdvance.</summary>
    NotFoundOrForbidden,

    /// <summary>Ghi chú ĐÃ GỬI đi (Dev đang sửa / BA đã nhận) — thu hồi lúc này chỉ làm lệch lịch sử.</summary>
    AlreadyDispatched,

    /// <summary>Bản demo đã được nghiệm thu ⇒ nội dung đang khoá (xem <see cref="PocAcceptanceGate"/>).</summary>
    PocAccepted
}

/// <summary>
/// THU HỒI một ghi chú (nút 🗑 trên trang review). Cùng quy tắc sở hữu với Feedback: chủ ghi chú thu hồi
/// được của mình, người có DeliveryAdvance (người duyệt cổng) thu hồi được mọi ghi chú của project.
/// <para>
/// Chỉ thu hồi được ghi chú còn <see cref="PocCommentStatus.Open"/>: đã gửi đi thì việc đã xảy ra (agent
/// đã sửa theo nó, hoặc BA đã nhận nó vào hội thoại) và giấu đi là nói dối lịch sử. Trong số ghi chú
/// Open, hai ca khác nhau hẳn nên được xử lý khác nhau:
/// </para>
/// <list type="bullet">
///   <item><b>Chưa từng gửi đi</b> (<see cref="WasDispatched"/> = false: không <c>Route</c>, không vòng sửa nào
///   đụng tới) — <b>xoá thật</b>. Đây là ghi chú gõ nhầm, gõ thử, gõ trùng: chưa ai ngoài người gõ nhìn
///   thấy nó, nên nó không phải một sự kiện của dự án. Để lại một dòng gạch ngang "đã thu hồi" trong bảng
///   lịch sử chỉ làm loãng bảng bằng rác của chính người đang đọc.</item>
///   <item><b>Đã đi một vòng rồi được mở lại</b> ("vẫn chưa đạt" — xem ReopenPocCommentUseCase: trạng thái
///   về Open nhưng <c>RevisionTaskId</c> giữ nguyên làm bằng chứng) — <b>thu hồi mềm</b>: đóng dấu
///   <see cref="PocComment.WithdrawnAtUtc"/>, dòng ở lại bảng lịch sử với nhãn "đã thu hồi". Vòng sửa đã
///   chạy theo nó rồi; xoá dòng này là làm bàn giao của agent nói về một ghi chú không còn tồn tại.</item>
/// </list>
/// </summary>
public class WithdrawPocCommentUseCase
{
    private readonly AppDbContext _db;
    private readonly PocAcceptanceGate _acceptanceGate;

    public WithdrawPocCommentUseCase(AppDbContext db, PocAcceptanceGate acceptanceGate)
    {
        _db = db;
        _acceptanceGate = acceptanceGate;
    }

    public async Task<WithdrawPocCommentResult> ExecuteAsync(
        Guid id, string? currentUsername, bool canManage, CancellationToken cancellationToken = default)
    {
        var comment = await _db.PocComments.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (comment == null)
            return WithdrawPocCommentResult.NotFoundOrForbidden;

        if (!canManage && (currentUsername == null || comment.CreatedByUsername != currentUsername))
            return WithdrawPocCommentResult.NotFoundOrForbidden;

        // Khoá sau nghiệm thu: đọc project của CHÍNH ghi chú (action chỉ nhận id ghi chú, không có projectId).
        if (await _acceptanceGate.IsLockedAsync(comment.ProjectId, cancellationToken))
            return WithdrawPocCommentResult.PocAccepted;

        if (comment.WithdrawnAtUtc.HasValue)
            return WithdrawPocCommentResult.Ok; // đã thu hồi rồi — bấm hai lần không phải lỗi.

        if (comment.Status != PocCommentStatus.Open)
            return WithdrawPocCommentResult.AlreadyDispatched;

        if (!WasDispatched(comment))
        {
            await KeepHarvestCursorAlignedAsync(comment, cancellationToken);
            _db.PocComments.Remove(comment);
        }
        else
        {
            comment.WithdrawnAtUtc = DateTime.UtcNow;
            comment.WithdrawnByUsername = currentUsername;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return WithdrawPocCommentResult.Ok;
    }

    /// <summary>
    /// Ghi chú đã từng rời trang review: đã gửi một đường nào đó, hoặc đã có vòng sửa đụng tới. ĐÂY là chỗ
    /// duy nhất định nghĩa ranh giới "xoá thật / thu hồi mềm" — <see cref="ListPocCommentsQuery"/> đọc lại
    /// nó để nói trước cho người dùng biết nút 🗑 sẽ làm gì. Xét cả ba cột chứ không chỉ <c>Route</c> vì
    /// "vẫn chưa đạt" (ReopenPocCommentUseCase) trả <c>Route</c> về null nhưng giữ <c>RevisionTaskId</c>.
    /// </summary>
    public static bool WasDispatched(PocComment comment) =>
        comment.Route != null || comment.RevisionTaskId != null || comment.AddressedAtUtc != null;

    /// <summary>
    /// Con trỏ harvest (<c>Project.PocFeedbackHarvestedCount</c>, xem PocFeedbackMemoryService) là một SỐ
    /// LƯỢNG ghi chú đã cân nhắc, dùng <c>Skip()</c> trên tập ghi chú POC xếp theo <c>(CreatedAt, Id)</c> —
    /// nó đếm đúng chỉ khi tập ấy không ngắn lại. Xoá thật một dòng NẰM TRƯỚC con trỏ làm mọi ghi chú sau
    /// nó trượt lên một bậc, và lượt harvest kế tiếp sẽ nhảy qua đúng một ghi chú mới chưa ai học. Lùi con
    /// trỏ một bậc để nó trỏ lại đúng ranh giới cũ.
    /// </summary>
    private async Task KeepHarvestCursorAlignedAsync(PocComment comment, CancellationToken cancellationToken)
    {
        // Chỉ ghi chú POC nằm trong tập con trỏ đếm (ghi chú Brief đi đường ChecklistGapMemoryService, lọc
        // theo BriefVersion nên không có con trỏ nào để lệch).
        if (comment.Target != PocCommentTarget.Poc)
            return;

        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == comment.ProjectId, cancellationToken);
        if (project == null || project.PocFeedbackHarvestedCount <= 0)
            return;

        // Nạp danh sách id theo ĐÚNG thứ tự của truy vấn harvest rồi tìm vị trí trong bộ nhớ: so sánh
        // Guid bằng SQL không dịch được đồng nhất giữa SQL Server và Sqlite, còn trần 300 ghi chú/dự án
        // khiến danh sách này luôn nhỏ.
        var orderedIds = await _db.PocComments.AsNoTracking()
            .Where(c => c.ProjectId == comment.ProjectId && c.Target == PocCommentTarget.Poc)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        var position = orderedIds.IndexOf(comment.Id);
        if (position >= 0 && position < project.PocFeedbackHarvestedCount)
            project.PocFeedbackHarvestedCount--;
    }
}
