using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Notifications;

/// <summary>
/// Một sự kiện thông báo đã dựng sẵn để đẩy ra các kênh NGOÀI (Teams/email). Khác <c>Notification</c>
/// (bản ghi in-app theo từng người): đây là thông điệp broadcast, không gắn người nhận cụ thể, và mang
/// <see cref="Url"/> TUYỆT ĐỐI (đã ghép BaseUrl) để bấm mở được từ Teams/email.
/// </summary>
public sealed record NotificationMessage(
    NotificationType Type,
    string Title,
    string Message,
    string? ProjectName,
    string? Url,
    // Email cá nhân của những user đã opt-in nhận email cho sự kiện này (ngoài danh sách To cố định của
    // admin). Kênh email gộp hai nguồn; các kênh khác (Teams) bỏ qua. null/rỗng ⇒ chỉ dùng danh sách To.
    IReadOnlyList<string>? EmailRecipients = null);
