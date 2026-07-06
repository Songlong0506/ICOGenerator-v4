using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Notifications;

/// <summary>
/// Đánh dấu MỘT thông báo là đã đọc. Ràng theo <paramref name="username"/> để một người không sửa được
/// thông báo của người khác. Trả về <see cref="Link"/> của thông báo (nếu có) để controller điều hướng.
/// </summary>
public class MarkNotificationReadUseCase
{
    private readonly AppDbContext _db;

    public MarkNotificationReadUseCase(AppDbContext db) => _db = db;

    public async Task<string?> ExecuteAsync(Guid id, string username, CancellationToken cancellationToken = default)
    {
        var notification = await _db.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.RecipientUsername == username, cancellationToken);

        if (notification == null)
            return null;

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return notification.Link;
    }
}
