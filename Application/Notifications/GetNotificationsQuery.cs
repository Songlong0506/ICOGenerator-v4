using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Notifications;

/// <summary>
/// Đọc thông báo của MỘT người dùng (theo username). Trả về số chưa đọc (cho badge) + N thông báo mới
/// nhất. Dùng cho cả chuông (onlyUnread + take nhỏ) lẫn trang inbox (toàn bộ, take lớn hơn).
/// </summary>
public class GetNotificationsQuery
{
    private readonly AppDbContext _db;

    public GetNotificationsQuery(AppDbContext db) => _db = db;

    public async Task<NotificationInboxVm> ExecuteAsync(string username, bool onlyUnread = false, int take = 30, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return new NotificationInboxVm(0, Array.Empty<NotificationVm>());

        var unreadCount = await _db.Notifications
            .CountAsync(n => n.RecipientUsername == username && !n.IsRead, cancellationToken);

        var query = _db.Notifications
            .Where(n => n.RecipientUsername == username);

        if (onlyUnread)
            query = query.Where(n => !n.IsRead);

        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .Select(n => new NotificationVm(
                n.Id,
                n.Type,
                n.Type.GetTitle(),
                n.Title,
                n.Message,
                n.ProjectName,
                n.Link,
                n.IsRead,
                n.CreatedAt))
            .ToListAsync(cancellationToken);

        return new NotificationInboxVm(unreadCount, items);
    }
}
