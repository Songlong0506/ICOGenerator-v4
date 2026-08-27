using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum WithdrawPocCommentResult
{
    Ok,

    /// <summary>Không có ghi chú đó, hoặc người gọi không phải chủ ghi chú và cũng không có DeliveryAdvance.</summary>
    NotFoundOrForbidden,

    /// <summary>Ghi chú ĐÃ GỬI đi (Dev đang sửa / BA đã nhận) — thu hồi lúc này chỉ làm lệch lịch sử.</summary>
    AlreadyDispatched
}

/// <summary>
/// THU HỒI một ghi chú (nút 🗑 trên trang review). Cùng quy tắc sở hữu với Feedback: chủ ghi chú thu hồi
/// được của mình, người có DeliveryAdvance (người duyệt cổng) thu hồi được mọi ghi chú của project.
/// <para>
/// Trước đây đây là XOÁ CỨNG (<c>_db.PocComments.Remove</c>): ghi chú biến mất khỏi DB, không còn dấu vết
/// ai bỏ và bỏ lúc nào, và mọi câu hỏi "bản V1 từng bị chê gì" sau đó không trả lời được. Nay dòng ở lại,
/// chỉ đóng dấu <c>WithdrawnAtUtc</c> — nó rời danh sách làm việc nhưng vẫn hiện trong bảng lịch sử với
/// nhãn "đã thu hồi". Đổi lại, thu hồi chỉ áp dụng cho ghi chú CHƯA gửi đi: đã gửi rồi thì việc đã xảy ra
/// (agent đã sửa theo nó, hoặc BA đã nhận nó vào hội thoại) và giấu đi là nói dối lịch sử.
/// </para>
/// </summary>
public class WithdrawPocCommentUseCase
{
    private readonly AppDbContext _db;

    public WithdrawPocCommentUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<WithdrawPocCommentResult> ExecuteAsync(
        Guid id, string? currentUsername, bool canManage, CancellationToken cancellationToken = default)
    {
        var comment = await _db.PocComments.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (comment == null)
            return WithdrawPocCommentResult.NotFoundOrForbidden;

        if (!canManage && (currentUsername == null || comment.CreatedByUsername != currentUsername))
            return WithdrawPocCommentResult.NotFoundOrForbidden;

        if (comment.WithdrawnAtUtc.HasValue)
            return WithdrawPocCommentResult.Ok; // đã thu hồi rồi — bấm hai lần không phải lỗi.

        if (comment.Status != Domain.Enums.PocCommentStatus.Open)
            return WithdrawPocCommentResult.AlreadyDispatched;

        comment.WithdrawnAtUtc = DateTime.UtcNow;
        comment.WithdrawnByUsername = currentUsername;
        await _db.SaveChangesAsync(cancellationToken);
        return WithdrawPocCommentResult.Ok;
    }
}
