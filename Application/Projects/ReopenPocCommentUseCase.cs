using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum ReopenPocCommentResult { Ok, NotFound, NotAddressed }

/// <summary>
/// "Vẫn chưa đạt": người review mở lại một ghi chú mà vòng chỉnh sửa POC đã tuyên bố xử lý xong
/// (<see cref="PocCommentStatus.Addressed"/>) — đưa nó về <see cref="PocCommentStatus.Open"/> để nó được
/// gom vào yêu cầu chỉnh sửa TIẾP THEO.
///
/// Trước đây vòng phản hồi chỉ có một chiều: ghi chú đi vào vòng sửa (Sent) rồi nằm im ở đó mãi mãi, dù
/// agent có sửa đúng hay không. Không mở lại được nghĩa là muốn nhắc lại cùng một lỗi thì phải ghim một
/// ghi chú mới — và danh sách phình lên bằng các bản sao của cùng một vấn đề.
/// </summary>
public class ReopenPocCommentUseCase
{
    private readonly AppDbContext _db;

    public ReopenPocCommentUseCase(AppDbContext db)
    {
        _db = db;
    }

    /// <param name="projectId">Project mà caller ĐÃ kiểm quyền truy cập — ghi chú phải thuộc đúng project này.</param>
    public async Task<ReopenPocCommentResult> ExecuteAsync(Guid projectId, Guid commentId, CancellationToken cancellationToken = default)
    {
        var comment = await _db.PocComments.FirstOrDefaultAsync(x => x.Id == commentId && x.ProjectId == projectId, cancellationToken);
        if (comment == null)
            return ReopenPocCommentResult.NotFound;

        // Ghi chú đã được ĐỊNH TUYẾN NGƯỢC về bước Requirement là một đường khác hẳn (POC sẽ được dựng
        // lại từ tài liệu đã sửa) — mở lại ở đây chỉ làm nó bị sửa hai lần theo hai cách.
        if (comment.Status != PocCommentStatus.Addressed && comment.Status != PocCommentStatus.Sent)
            return ReopenPocCommentResult.NotAddressed;

        comment.Status = PocCommentStatus.Open;
        comment.AddressedAtUtc = null;
        comment.AddressedNote = null;
        await _db.SaveChangesAsync(cancellationToken);

        return ReopenPocCommentResult.Ok;
    }
}
