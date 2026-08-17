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
/// <b>Danh sách người nhận được LƯU CÙNG LƯỢT với bảng.</b> Nó là một bảng người dùng sửa trực tiếp (thêm /
/// sửa chữ / xóa), nên nó phải tới server ở chính lượt gửi này: giữ nó ở riêng trình duyệt thì mọi mục
/// người dùng tự thêm bị <c>NormalizeRecipients</c> bỏ sạch ngay lúc lưu — bảng hiện rõ tên người nhận ở
/// từng dòng mà server lại trả về "còn N sự kiện chưa chọn người nhận", một ca không ai gỡ được. Bộ đối
/// chiếu vì vậy là danh sách VỪA GỬI LÊN đã chuẩn hoá (<see cref="NotificationMapBuilder.SanitizeRecipients"/>)
/// chứ không phải bộ tùy chọn thô trong payload, và nó được ghi xuống cột cùng một
/// <c>SaveChangesAsync</c> với bảng — hai thứ lệch nhau thì lần mở lại nào cũng thấy một bảng chọn từ một
/// danh sách khác.
/// </para>
///
/// <para>
/// Danh sách rỗng (payload cũ từ tab mở trước bản này, hoặc người dùng xóa sạch) KHÔNG được hiểu là "dự án
/// không còn người nhận nào": nó rơi về đúng bộ mà lượt bày bảng đã dùng — cột đã lưu, hoặc bản gieo từ
/// bảng phân quyền.
/// </para>
///
/// <para>
/// <b>Chốt chặn BẤT BIẾN của bảng nằm ở đây, không ở trình duyệt</b> (xem
/// <see cref="NotificationMapBuilder.MissingRecipients"/>): còn một dòng tích "Cần" mà chưa chọn người nhận
/// thì KHÔNG lưu gì cả và trả về đúng tên các sự kiện còn thiếu. Popup của trình duyệt là phanh phụ — nó
/// không thấy được payload sửa tay, tab mở từ trước bản này, hay lần bấm gửi lại sau khi mất mạng. Và lưu
/// một phần thì tệ hơn không lưu: cột <c>NotificationMap</c> có dữ liệu ⇒ <c>NotificationMapGate</c> coi
/// như đã chốt và không bao giờ bày lại bảng, nên các dòng còn dở không còn màn hình nào để sửa.
/// </para>
/// </summary>
public class ConfirmNotificationMapUseCase
{
    private readonly AppDbContext _db;

    public ConfirmNotificationMapUseCase(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Số sự kiện đã lưu + tin nhắn mà trình duyệt phải gửi tiếp vào khung chat. <paramref name="Error"/>
    /// khác rỗng ⇒ KHÔNG lưu gì, và chuỗi đó là câu hiện ngay cạnh nút gửi (đã gọi tên các sự kiện còn
    /// thiếu, nên trình duyệt chỉ việc in ra).
    /// </summary>
    public sealed record Result(int Rows, string Message, string Error = "");

    public async Task<Result> ExecuteAsync(
        Guid projectId,
        string? notificationsJson,
        string? recipientsJson,
        CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return new Result(0, string.Empty);

        var options = NotificationMapBuilder.SanitizeRecipients(
            NotificationMapBuilder.ParseRecipients(recipientsJson));
        if (options.Count == 0)
            options = NotificationMapBuilder.RecipientOptions(
                project.NotificationRecipients, PermissionMatrixBuilder.Roles(project.PermissionMatrix));

        var rows = NotificationMapBuilder.Sanitize(NotificationMapBuilder.Parse(notificationsJson), options);
        if (rows.Count == 0)
            return new Result(0, string.Empty);

        var missing = NotificationMapBuilder.MissingRecipients(rows);
        if (missing.Count > 0)
            return new Result(0, string.Empty,
                $"Còn {missing.Count} sự kiện đã tích \"Cần\" nhưng chưa chọn người nhận: "
                + string.Join("; ", missing.Select(NotificationMapBuilder.EventLabel))
                + ". Anh/chị chọn người nhận, hoặc bỏ tích nếu sự kiện đó không cần gửi email, rồi gửi lại nhé.");

        project.NotificationMap = JsonSerializer.Serialize(rows);
        project.NotificationRecipients = JsonSerializer.Serialize(options);
        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, NotificationMapBuilder.RenderUserMessage(rows));
    }
}
