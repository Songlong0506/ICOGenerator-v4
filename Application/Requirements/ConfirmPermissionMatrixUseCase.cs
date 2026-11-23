using ICOGenerator.Localization;
using System.Text.Json;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// LƯU bảng phân quyền người dùng vừa sửa ở popup — popup mở từ nút «Xem bảng phân quyền» dưới panel Tiến
/// độ khai thác, trên bảng mà BA đã tự soạn từ hội thoại (<see cref="PermissionMatrixDraftService"/>). Lưu vào
/// <see cref="ICOGenerator.Domain.Project.PermissionMatrix"/> — từ đó <see cref="BAChatService"/> và prompt sinh
/// AI Design Spec đọc ra.
///
/// <para>
/// Use case này CHỈ lưu: không gọi LLM, không ghi lượt hội thoại nào, và trình duyệt cũng KHÔNG gửi tin nhắn
/// nào tiếp vào khung chat (cùng khuôn với bảng cột — <see cref="ConfirmSourceColumnMapUseCase"/>). Sửa vài
/// ô quyền không phải một lượt phỏng vấn: BA đọc lại bảng ở khối "bảng phân quyền" của mọi lượt chat sau,
/// và bước sinh AI Design Spec đọc thẳng cột này.
/// </para>
///
/// <para>
/// <b>Danh sách VAI TRÒ đi cùng chuyến với bảng.</b> Nó là một bảng người dùng sửa trực tiếp (thêm / sửa
/// chữ / xóa) và là nguồn của mọi CỘT bên dưới, nên nó phải tới server ở chính lượt gửi này — để server tự
/// chắt cột từ grants thì một vai người dùng vừa thêm nhưng chưa cấp quyền ở dòng nào sẽ biến mất khỏi bảng
/// đã lưu mà họ không nhận ra.
/// </para>
///
/// <para>
/// Không vai nào ⇒ KHÔNG lưu gì và trả về câu nói rõ lý do: bảng không có cột thì mọi ô quyền biến mất, mà
/// cột <c>PermissionMatrix</c> có dữ liệu là <see cref="PermissionMatrixGate"/> coi như đã có bảng và không
/// bao giờ soạn lại.
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
    /// Kết quả lưu bảng: số dòng đã lưu. <paramref name="Error"/> khác rỗng ⇒ KHÔNG lưu gì, và chuỗi đó là
    /// câu hiện ngay cạnh nút lưu.
    /// </summary>
    public sealed record Result(int Rows, string Error = "");

    public async Task<Result> ExecuteAsync(
        Guid projectId,
        string? matrixJson,
        string? rolesJson = null,
        CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return new Result(0);

        // Bộ CỘT của bảng — xem ghi chú class. Rỗng thì builder rơi về bản chắt từ grants (tab cũ), nên chỉ
        // chặn khi client CÓ gửi bảng vai trò mà nó trống trơn: đó là người dùng vừa xóa hết vai.
        var roles = PermissionMatrixBuilder.SanitizeRoles(PermissionMatrixBuilder.ParseRoles(rolesJson));
        if (roles.Count == 0 && !string.IsNullOrWhiteSpace(rolesJson))
            return new Result(0, Loc.T("ConfirmPermissionMatrixUseCase.ExecuteAsync"));

        // Server KHÔNG tin bảng client gửi, kể cả khi chính server vừa render nó ra: dòng phải là tác vụ có thật
        // của bản thiết kế UX (khoá theo ID tác vụ), và mọi dòng phải đủ vai trò (xem PermissionMatrixBuilder).
        var rows = PermissionMatrixBuilder.Sanitize(
            PermissionMatrixBuilder.Parse(matrixJson), UxBlueprintBuilder.Parse(project.UxBlueprint), roles);
        if (rows.Count == 0)
            return new Result(0);

        project.PermissionMatrix = JsonSerializer.Serialize(rows);
        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count);
    }
}
