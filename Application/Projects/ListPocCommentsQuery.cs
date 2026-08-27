using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

/// <summary>
/// Một ghi chú ghim trên POC, ở dạng client render được. CanDelete tính sẵn phía server (chủ ghi chú
/// hoặc người có DeliveryAdvance) để JS không phải đoán quyền — nó chi phối nút "thu hồi", KHÔNG phải
/// xoá: dòng lịch sử không bao giờ mất (xem WithdrawPocCommentUseCase).
/// </summary>
public record PocCommentItem(
    Guid Id,
    string PageView,
    string ElementLabel,
    string ElementPath,
    double XPercent,
    double YPercent,
    string Comment,
    string Status,
    string? CreatedBy,
    DateTime CreatedAt,
    bool CanDelete,
    DateTime? AddressedAt,
    string? AddressedNote,
    // Bản Product Brief mà ghi chú nói về ("V2") — hiện thành nhãn trên từng mục để vòng review thứ hai
    // trở đi phân biệt được ghi chú của bản demo đang xem với ghi chú thế hệ trước.
    string BriefVersion,
    // Đường đã gửi đi ("FixPoc"/"Requirement"), null = chưa gửi.
    string? Route);

public class ListPocCommentsQuery
{
    private readonly AppDbContext _db;

    public ListPocCommentsQuery(AppDbContext db)
    {
        _db = db;
    }

    /// <param name="currentUsername">User đang xem — quyết định CanDelete cho ghi chú của chính họ.</param>
    /// <param name="canManage">True khi user có DeliveryAdvance (xóa được mọi ghi chú).</param>
    public async Task<List<PocCommentItem>> ExecuteAsync(
        Guid projectId, string? currentUsername, bool canManage, CancellationToken cancellationToken = default)
    {
        // Đường LÀM VIỆC của trang review: chỉ ghi chú POC còn hiệu lực. Ghi chú Brief và các dòng đã thu
        // hồi vẫn còn nguyên trong DB nhưng thuộc về bảng lịch sử (GetPocNoteHistoryQuery), không phải
        // danh sách pin — pin của chúng không neo vào phần tử nào trong bản demo.
        var comments = await _db.PocComments.AsNoTracking()
            .Where(x => x.ProjectId == projectId
                        && x.Target == PocCommentTarget.Poc
                        && x.WithdrawnAtUtc == null)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return comments.Select(x => new PocCommentItem(
            x.Id,
            x.PageView,
            x.ElementLabel,
            x.ElementPath,
            x.XPercent,
            x.YPercent,
            x.Comment,
            x.Status.ToString(),
            x.CreatedByUsername,
            x.CreatedAt,
            canManage || (currentUsername != null && x.CreatedByUsername == currentUsername),
            x.AddressedAtUtc,
            x.AddressedNote,
            x.BriefVersion,
            x.Route?.ToString()))
            .ToList();
    }
}
