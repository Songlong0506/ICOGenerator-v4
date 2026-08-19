using System.Security.Claims;
using ICOGenerator.Application.Notifications;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Notifications;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Notifications;

// NotificationService ĐANG TẮT TẠM THỜI (NotificationService.Enabled = false): cách chọn người nhận cũ lọc
// theo quyền DeliveryAdvance, mà quyền suy ra từ vai trò, còn vai trò nay chỉ tồn tại trong claim của phiên
// đăng nhập — người cần được báo thì đang offline. Test ở đây ghim trạng thái "không gửi gì cả"; phần
// đọc/đánh dấu đã đọc (ràng theo chủ sở hữu) vẫn chạy bình thường vì nó không phụ thuộc đường gửi.
// Chạy trên AppDbContext thật (Sqlite).
public class NotificationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public NotificationServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task MarkRead_IsScopedToOwner()
    {
        var id = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.Notifications.Add(new Notification { Id = id, RecipientUsername = "teamdev", Title = "t", Message = "m", Link = "/x" });
            await db.SaveChangesAsync();
        }

        // Người khác không đánh dấu được thông báo của teamdev.
        await using (var db = NewDb())
        {
            var link = await new MarkNotificationReadUseCase(db).ExecuteAsync(id, "someone-else");
            Assert.Null(link);
        }
        await using (var db = NewDb())
            Assert.False(await db.Notifications.Where(n => n.Id == id).Select(n => n.IsRead).FirstAsync());

        // Chủ sở hữu đánh dấu được và nhận lại Link để điều hướng.
        await using (var db = NewDb())
        {
            var link = await new MarkNotificationReadUseCase(db).ExecuteAsync(id, "teamdev");
            Assert.Equal("/x", link);
        }
        await using (var db = NewDb())
            Assert.True(await db.Notifications.Where(n => n.Id == id).Select(n => n.IsRead).FirstAsync());
    }

    [Fact]
    public async Task MarkAllRead_UpdatesOnlyOwnersUnread_AndReturnsCount()
    {
        await using (var db = NewDb())
        {
            db.Notifications.AddRange(
                new Notification { RecipientUsername = "teamdev", Title = "a", IsRead = false },
                new Notification { RecipientUsername = "teamdev", Title = "b", IsRead = false },
                new Notification { RecipientUsername = "teamdev", Title = "c", IsRead = true },
                new Notification { RecipientUsername = "other", Title = "d", IsRead = false });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
            Assert.Equal(2, await new MarkAllNotificationsReadUseCase(db).ExecuteAsync("teamdev"));

        await using (var db = NewDb())
        {
            // Toàn bộ thông báo của teamdev đã đọc (có ReadAt); của người khác giữ nguyên.
            Assert.False(await db.Notifications.AnyAsync(n => n.RecipientUsername == "teamdev" && !n.IsRead));
            Assert.True(await db.Notifications.Where(n => n.RecipientUsername == "teamdev" && n.Title != "c").AllAsync(n => n.ReadAt != null));
            Assert.False(await db.Notifications.Where(n => n.RecipientUsername == "other").Select(n => n.IsRead).FirstAsync());
        }

        // Không còn gì chưa đọc ⇒ 0 dòng cập nhật.
        await using (var db = NewDb())
            Assert.Equal(0, await new MarkAllNotificationsReadUseCase(db).ExecuteAsync("teamdev"));
    }

    [Fact]
    public async Task GetNotifications_ReturnsUnreadCountAndItemsForUserOnly()
    {
        await using (var db = NewDb())
        {
            db.Notifications.AddRange(
                new Notification { RecipientUsername = "teamdev", Title = "a", IsRead = false, CreatedAt = DateTime.UtcNow.AddMinutes(-1) },
                new Notification { RecipientUsername = "teamdev", Title = "b", IsRead = true, CreatedAt = DateTime.UtcNow.AddMinutes(-2) },
                new Notification { RecipientUsername = "other", Title = "c", IsRead = false });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var inbox = await new GetNotificationsQuery(db).ExecuteAsync("teamdev");
            Assert.Equal(1, inbox.UnreadCount);
            Assert.Equal(2, inbox.Items.Count);
            // Mới nhất trước.
            Assert.Equal("a", inbox.Items[0].Title);
        }
    }

    // Hợp đồng hiện tại: NotificationService ĐANG TẮT (NotificationService.Enabled = false) nên không đường
    // vào nào ghi dòng Notifications hay gọi kênh ngoài. Hai test dưới GHIM đúng trạng thái đó — khi bật lại,
    // chúng phải fail và được thay bằng test cho tiêu chí chọn người nhận mới.
    [Fact]
    public async Task AllNotifyMethods_CreateNothing_WhileDisabled()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.AppUsers.AddRange(
                new AppUser { Username = "admin" },
                new AppUser { Username = "teamdev", NotifyInApp = true, NotifyByEmail = true, Email = "e@bosch.com" },
                new AppUser { Username = "user" });
            db.Projects.Add(new Project { Id = projectId, Name = "Cổng thanh toán" });
            db.WorkflowRuns.Add(new WorkflowRun { Id = runId, ProjectId = projectId, Status = WorkflowRunStatus.WaitingForHuman });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var run = await db.WorkflowRuns.FirstAsync(r => r.Id == runId);
            var svc = new NotificationService(db, Array.Empty<INotificationChannel>(), new NotificationOptions(), NullLogger<NotificationService>.Instance);

            await svc.NotifyGateOpenedAsync(run, "Đề xuất kiến trúc");
            await svc.NotifyRunCompletedAsync(run);
            await svc.NotifyRunFailedAsync(run, "boom");
            await svc.NotifyPocAcceptedAsync(run, "teamdev");
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
            Assert.Equal(0, await db.Notifications.CountAsync());
    }

    [Fact]
    public async Task Disabled_DoesNotTouchExternalChannels()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.AppUsers.Add(new AppUser { Username = "teamdev" });
            db.Projects.Add(new Project { Id = projectId, Name = "Cổng thanh toán" });
            db.WorkflowRuns.Add(new WorkflowRun { Id = runId, ProjectId = projectId });
            await db.SaveChangesAsync();
        }

        var enabled = new RecordingChannel(isEnabled: true);
        var throwing = new ThrowingChannel();

        await using (var db = NewDb())
        {
            var run = await db.WorkflowRuns.FirstAsync(r => r.Id == runId);
            var svc = new NotificationService(
                db,
                new INotificationChannel[] { enabled, throwing },
                new NotificationOptions { BaseUrl = "https://app.example/" },
                NullLogger<NotificationService>.Instance);

            // Kênh ném lỗi có mặt: nếu đường gửi còn chạy, nó đã ném ra ở đây.
            await svc.NotifyGateOpenedAsync(run, "Đề xuất kiến trúc");
            await db.SaveChangesAsync();
        }

        Assert.Null(enabled.Last);
    }

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class RecordingChannel : INotificationChannel
    {
        public RecordingChannel(bool isEnabled) => IsEnabled = isEnabled;
        public string Name => "Recording";
        public bool IsEnabled { get; }
        public NotificationMessage? Last { get; private set; }
        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            Last = message;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingChannel : INotificationChannel
    {
        public string Name => "Throwing";
        public bool IsEnabled => true;
        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
    }
}
