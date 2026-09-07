using System.Text;

namespace ICOGenerator.Services.Notifications.Channels;

/// <summary>
/// Tiêu đề và thân của một email thông báo — dùng CHUNG cho hai đường gửi mail:
/// <see cref="EmailNotificationChannel"/> (SMTP trực tiếp) và
/// <see cref="BoschEmailServerNotificationChannel"/> (Email Server API nội bộ).
/// <para>
/// Hai kênh khác nhau ở CÁCH gửi, không khác ở NỘI DUNG: người nhận cùng một sự kiện phải đọc được đúng
/// một lá thư dù hạ tầng nào đang bật. Trước đây mỗi kênh giữ một bản <c>BuildSubject</c>/<c>BuildBody</c>
/// riêng, nên đổi văn phong ở một bên là hai môi trường nhận hai lá thư khác nhau.
/// </para>
/// </summary>
internal static class EmailNotificationText
{
    // Tiền tố "[ICOGen]" để lọc/gom thư trong hộp thư; tên dự án đi kèm tiêu đề vì phần lớn người nhận
    // theo dõi nhiều dự án cùng lúc.
    public static string Subject(NotificationMessage message) =>
        string.IsNullOrWhiteSpace(message.ProjectName)
            ? $"[ICOGen] {message.Title}"
            : $"[ICOGen] {message.Title} — {message.ProjectName}";

    // Thân thư dạng văn bản thuần (IsBodyHtml = false ở cả hai kênh): nội dung, rồi tên dự án, rồi link
    // TUYỆT ĐỐI mở Agent Dashboard. Thiếu BaseUrl ⇒ NotificationMessage.Url rỗng ⇒ bỏ hẳn dòng link.
    public static string Body(NotificationMessage message)
    {
        var body = new StringBuilder();
        body.AppendLine(message.Message);
        if (!string.IsNullOrWhiteSpace(message.ProjectName))
            body.AppendLine().Append("Project: ").AppendLine(message.ProjectName);
        if (!string.IsNullOrWhiteSpace(message.Url))
            body.AppendLine().Append("Mở Agent Dashboard: ").AppendLine(message.Url);
        return body.ToString();
    }
}
