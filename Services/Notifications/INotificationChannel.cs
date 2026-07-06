namespace ICOGenerator.Services.Notifications;

/// <summary>
/// Một kênh gửi thông báo NGOÀI (Teams, email, …). <see cref="NotificationService"/> fan-out một
/// <see cref="NotificationMessage"/> tới mọi kênh đang bật sau khi đã ghi thông báo in-app.
///
/// Hợp đồng: <see cref="SendAsync"/> phải TỰ fail-open (không ném ra ngoài) — một kênh hỏng/không kết nối
/// được KHÔNG bao giờ làm gãy workflow hay chặn các kênh khác. Kênh TẮT (<see cref="IsEnabled"/> = false)
/// sẽ không được gọi và không tốn tài nguyên (đây là "seam" opt-in).
/// </summary>
public interface INotificationChannel
{
    /// <summary>Tên ngắn để log (vd "Teams", "Email").</summary>
    string Name { get; }

    /// <summary>True nếu kênh đã được cấu hình đầy đủ và bật. False ⇒ bỏ qua hoàn toàn.</summary>
    bool IsEnabled { get; }

    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}
