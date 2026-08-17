using System.Text.Json;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// CHỐT bảng thông báo / nhắc nhở: người dùng bỏ tích các sự kiện không cần báo, chọn người nhận cho các
/// sự kiện còn lại rồi gửi. Lưu vào <see cref="ICOGenerator.Domain.Project.NotificationMap"/> — từ đó khối
/// "đã chốt" đi vào ngữ cảnh chat, vào lượt distill bản đồ bao phủ (nhóm «Thông báo / nhắc nhở» mới có căn
/// cứ để <c>[RÕ]</c>) và vào prompt sinh AI Design Spec.
///
/// <para>
/// Cùng khuôn HAI BƯỚC với <see cref="ConfirmPermissionMatrixUseCase"/>: chỉ lưu, không gọi LLM; tin nhắn
/// kể lại do server soạn từ bảng đã chuẩn hoá rồi trình duyệt gửi qua đúng đường chat thường.
/// </para>
///
/// <para>
/// Danh sách người nhận hợp lệ được dựng LẠI ở đây từ bảng phân quyền đã chốt chứ không lấy từ payload:
/// server không tin bộ tùy chọn mà trình duyệt gửi kèm, kể cả khi chính server vừa render nó ra vài phút
/// trước — nếu không thì một payload sửa tay đưa được người nhận bất kỳ vào tài liệu và vào POC.
/// </para>
/// </summary>
public class ConfirmNotificationMapUseCase
{
    private readonly AppDbContext _db;

    public ConfirmNotificationMapUseCase(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Số sự kiện đã lưu + tin nhắn mà trình duyệt phải gửi tiếp vào khung chat.</summary>
    public sealed record Result(int Rows, string Message);

    public async Task<Result> ExecuteAsync(Guid projectId, string? notificationsJson, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return new Result(0, string.Empty);

        var options = NotificationMapBuilder.RecipientOptions(
            PermissionMatrixBuilder.Roles(project.PermissionMatrix));

        var rows = NotificationMapBuilder.Sanitize(NotificationMapBuilder.Parse(notificationsJson), options);
        if (rows.Count == 0)
            return new Result(0, string.Empty);

        project.NotificationMap = JsonSerializer.Serialize(rows);
        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, NotificationMapBuilder.RenderUserMessage(rows));
    }
}
