using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// ĐỌC bảng phân quyền đang lưu của dự án cho popup "Phân quyền theo nghiệp vụ" — popup mở khi người dùng bấm
/// vào dòng đó trên panel Tiến độ khai thác, sau khi BA đã tự soạn bảng (<see cref="PermissionMatrixDraftService"/>).
///
/// <para>
/// Popup đọc bảng TƯƠI mỗi lần mở chứ không dựng từ bản server render lúc tải trang: bảng có thể vừa được BA
/// soạn ở một lượt chat chạy qua SSE (trang không tải lại), hoặc vừa được chính người dùng lưu ở lần mở
/// popup trước. Một nguồn duy nhất — cột <c>Project.PermissionMatrix</c> — thì không có hai bản để lệch nhau.
/// </para>
/// </summary>
public class GetPermissionMatrixQuery
{
    private readonly AppDbContext _db;

    public GetPermissionMatrixQuery(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Các dòng của bảng (màn hình × chức năng, mỗi dòng đủ mọi vai) và bộ vai trò = CỘT của bảng, theo đúng
    /// thứ tự đã lưu. Cả hai rỗng khi dự án chưa có bảng (hoặc không tồn tại).
    /// </summary>
    public sealed record Result(List<PermissionMatrixRow> Rows, List<string> Roles);

    public async Task<Result> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var json = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.PermissionMatrix)
            .FirstOrDefaultAsync(cancellationToken);

        return new Result(PermissionMatrixBuilder.Parse(json), PermissionMatrixBuilder.Roles(json));
    }
}
