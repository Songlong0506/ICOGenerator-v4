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

        // Một câu UPDATE thay vì nạp toàn bộ entity (kèm Message dài) về chỉ để flip 2 cột.
        var now = DateTime.UtcNow;
        return await _db.Notifications
            .Where(n => n.RecipientUsername == username && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, now), cancellationToken);
    }
}
