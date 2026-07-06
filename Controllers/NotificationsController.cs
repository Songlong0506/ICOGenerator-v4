using ICOGenerator.Application.Notifications;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

/// <summary>
/// Inbox thông báo CÁ NHÂN: mỗi người đăng nhập chỉ xem/đánh dấu thông báo của chính mình (ràng theo
/// username ở tầng use case). Không gắn <c>[RequirePermission]</c> — fallback policy đã bắt phải đăng nhập,
/// và chỉ những user có quyền DeliveryAdvance mới thực sự nhận được dòng thông báo nào.
/// </summary>
public class NotificationsController : Controller
{
    private readonly GetNotificationsQuery _getNotifications;
    private readonly MarkNotificationReadUseCase _markRead;
    private readonly MarkAllNotificationsReadUseCase _markAllRead;

    public NotificationsController(
        GetNotificationsQuery getNotifications,
        MarkNotificationReadUseCase markRead,
        MarkAllNotificationsReadUseCase markAllRead)
    {
        _getNotifications = getNotifications;
        _markRead = markRead;
        _markAllRead = markAllRead;
    }

    // Trang inbox đầy đủ.
    public async Task<IActionResult> Index()
    {
        var inbox = await _getNotifications.ExecuteAsync(User.Identity?.Name ?? string.Empty, onlyUnread: false, take: 50, HttpContext.RequestAborted);
        return View(inbox);
    }

    // JSON cho chuông trên topbar (poll định kỳ): số chưa đọc + vài thông báo gần nhất.
    [HttpGet]
    public async Task<IActionResult> Feed()
    {
        var inbox = await _getNotifications.ExecuteAsync(User.Identity?.Name ?? string.Empty, onlyUnread: false, take: 10, HttpContext.RequestAborted);
        return Json(new
        {
            unreadCount = inbox.UnreadCount,
            items = inbox.Items.Select(n => new
            {
                id = n.Id,
                type = n.Type.ToString(),
                title = n.Title,
                message = n.Message,
                projectName = n.ProjectName,
                link = n.Link,
                isRead = n.IsRead,
                createdAt = n.CreatedAt
            })
        });
    }

    // Mở một thông báo: đánh dấu đã đọc rồi điều hướng tới Link của nó (hoặc về inbox nếu không có link).
    [HttpGet]
    public async Task<IActionResult> Open(Guid id)
    {
        var link = await _markRead.ExecuteAsync(id, User.Identity?.Name ?? string.Empty, HttpContext.RequestAborted);
        if (!string.IsNullOrWhiteSpace(link) && Url.IsLocalUrl(link))
            return Redirect(link);
        return RedirectToAction(nameof(Index));
    }

    // Đánh dấu tất cả đã đọc (gọi từ chuông qua fetch). AutoValidateAntiforgeryToken toàn cục bắt token —
    // JS gửi kèm header RequestVerificationToken.
    [HttpPost]
    public async Task<IActionResult> MarkAllRead()
    {
        var updated = await _markAllRead.ExecuteAsync(User.Identity?.Name ?? string.Empty, HttpContext.RequestAborted);
        return Json(new { ok = true, updated });
    }
}
