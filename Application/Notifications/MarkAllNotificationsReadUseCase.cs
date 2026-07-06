using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Notifications;

/// <summary>Đánh dấu TẤT CẢ thông báo chưa đọc của một người dùng là đã đọc. Trả về số dòng vừa cập nhật.</summary>
public class MarkAllNotificationsReadUseCase
{
    private readonly AppDbContext _db;

    public MarkAllNotificationsReadUseCase(AppDbContext db) => _db = db;

    public async Task<int> ExecuteAsync(string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return 0;

        var unread = await _db.Notifications
            .Where(n => n.RecipientUsername == username && !n.IsRead)
            .ToListAsync(cancellationToken);

        if (unread.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        foreach (var n in unread)
        {
            n.IsRead = true;
            n.ReadAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return unread.Count;
    }
}
