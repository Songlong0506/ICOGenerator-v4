using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// CHỐT bảng phân quyền: người dùng chọn phạm vi cho từng ô (vai trò × chức năng × màn hình) rồi gửi. Lưu
/// vào <see cref="ICOGenerator.Domain.Project.PermissionMatrix"/> — từ đó <see cref="BAChatService"/>,
/// <see cref="RequirementCoverageService"/> và prompt sinh AI Design Spec đọc ra.
///
/// <para>
/// Use case này CHỈ lưu, không gọi LLM và không ghi lượt hội thoại nào — cùng khuôn với
/// <see cref="ConfirmSourceColumnMapUseCase"/>. Phần "kể lại cho BA nghe" đi đúng đường chat thường: trình
/// duyệt soạn bảng đã chốt thành một tin nhắn của người dùng rồi gửi qua khung chat. Nhờ vậy hội thoại vẫn
/// chỉ có MỘT đường ghi, và mọi thứ đã đúng ở lượt chat (cổng readiness, chắt lọc bản đồ bao phủ, nhật ký
/// điều đã chốt, bản Product Brief đọc transcript) tự khắc đúng ở đây.
/// </para>
///
/// <para>
/// <b>Danh sách VAI TRÒ đi cùng chuyến với bảng.</b> Nó là một bảng người dùng sửa trực tiếp (thêm / sửa
/// chữ / xóa) và là nguồn của mọi CỘT bên dưới, nên nó phải tới server ở chính lượt gửi này — giữ ở riêng
/// trình duyệt thì server lại chắt cột từ grants như cũ, và một vai người dùng vừa thêm nhưng chưa cấp
/// quyền ở dòng nào sẽ biến mất khỏi bảng đã lưu mà họ không nhận ra. Danh sách rỗng (tab mở từ trước bản
/// này) rơi về đúng hành vi cũ: cột chắt từ chính grants gửi lên.
/// </para>
///
/// <para>
/// Không vai nào ⇒ KHÔNG lưu gì và trả về câu nói rõ lý do. Bảng không có cột thì mọi ô quyền biến mất,
/// mà cột <c>PermissionMatrix</c> có dữ liệu là <see cref="PermissionMatrixGate"/> coi như đã chốt và
/// không bao giờ bày lại bảng — người dùng mất luôn đường sửa. Cùng luật fail-closed với
/// <see cref="ConfirmNotificationMapUseCase"/>.
/// </para>
/// </summary>
public class ConfirmPermissionMatrixUseCase
{
    private readonly AppDbContext _db;

    public ConfirmPermissionMatrixUseCase(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Kết quả chốt bảng: số dòng đã lưu + tin nhắn mà trình duyệt phải gửi tiếp vào khung chat. Server soạn
    /// tin nhắn (thay vì để JS tự ghép) vì nó phải khớp ĐÚNG bảng đã được chuẩn hoá và lưu — không phải bảng
    /// client vừa gửi lên: hai bản lệch nhau thì hội thoại kể một đằng còn dữ liệu dự án ghi một nẻo.
    ///
    /// <para>
    /// <paramref name="Error"/> khác rỗng ⇒ KHÔNG lưu gì, và chuỗi đó là câu hiện ngay cạnh nút gửi.
    /// </para>
    /// </summary>
    public sealed record Result(int Rows, string Message, string Error = "");

    public async Task<Result> ExecuteAsync(
        Guid projectId,
        string? matrixJson,
        string? rolesJson = null,
        CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return new Result(0, string.Empty);

        // Bộ CỘT của bảng — xem ghi chú class. Rỗng thì builder rơi về bản chắt từ grants (tab cũ), nên chỉ
        // chặn khi client CÓ gửi bảng vai trò mà nó trống trơn: đó là người dùng vừa xóa hết vai.
        var roles = PermissionMatrixBuilder.SanitizeRoles(PermissionMatrixBuilder.ParseRoles(rolesJson));
        if (roles.Count == 0 && !string.IsNullOrWhiteSpace(rolesJson))
            return new Result(0, string.Empty,
                "Bảng phải có ít nhất một vai trò — anh/chị thêm lại một dòng ở bảng Vai trò rồi gửi nhé.");

        // Server KHÔNG tin bảng client gửi, kể cả khi chính server vừa render nó ra: tên màn hình phải khớp
        // lại phạm vi đã chắt của dự án, và mọi dòng phải đủ vai trò (xem PermissionMatrixBuilder). Phạm vi
        // đối chiếu là phạm vi ĐÃ RÀ nếu bảng màn hình đã chốt — nếu dùng PlannedScope thô ở đây thì một
        // màn hình người dùng vừa bỏ tích vẫn khớp được, và quyền của nó đi thẳng vào tài liệu.
        var rows = PermissionMatrixBuilder.Sanitize(
            PermissionMatrixBuilder.Parse(matrixJson), PermissionMatrixGate.EffectiveScreens(project), roles);
        if (rows.Count == 0)
            return new Result(0, string.Empty);

        project.PermissionMatrix = JsonSerializer.Serialize(rows);
        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, PermissionMatrixBuilder.RenderUserMessage(rows));
    }
}
