using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Application.Notifications;

/// <summary>Một dòng thông báo để hiển thị ở chuông/inbox.</summary>
public sealed record NotificationVm(
    Guid Id,
    NotificationType Type,
    string TypeLabel,
    string Title,
    string Message,
    string? ProjectName,
    string? Link,
    bool IsRead,
    DateTime CreatedAt);

/// <summary>Dữ liệu cho chuông thông báo: số chưa đọc + danh sách thông báo gần đây.</summary>
public sealed record NotificationInboxVm(int UnreadCount, IReadOnlyList<NotificationVm> Items);
