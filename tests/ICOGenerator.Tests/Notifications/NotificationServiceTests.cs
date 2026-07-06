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

// NotificationService chỉ tạo thông báo cho user ĐANG HOẠT ĐỘNG có quyền DeliveryAdvance, và chỉ Add
// (không SaveChanges) — người gọi lưu. Đọc/đánh dấu ràng theo chủ sở hữu. Chạy trên AppDbContext thật (Sqlite).
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
    public async Task NotifyGateOpened_CreatesForActiveDeliveryAdvanceUsersOnly()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.AppUsers.AddRange(
                new AppUser { Username = "admin", Role = UserRole.Admin, IsActive = true },
                new AppUser { Username = "teamdev", Role = UserRole.TeamDev, IsActive = true },
                new AppUser { Username = "teamdev_off", Role = UserRole.TeamDev, IsActive = false },
                new AppUser { Username = "user", Role = UserRole.User, IsActive = true });
            db.Projects.Add(new Project { Id = projectId, Name = "Cổng thanh toán" });
            db.WorkflowRuns.Add(new WorkflowRun { Id = runId, ProjectId = projectId, Status = WorkflowRunStatus.WaitingForHuman });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var run = await db.WorkflowRuns.FirstAsync(r => r.Id == runId);
            var svc = new NotificationService(db, FakePermissions.WithDeliveryAdvanceFor(UserRole.Admin, UserRole.TeamDev), NullLogger<NotificationService>.Instance);

            await svc.NotifyGateOpenedAsync(run, "Đề xuất kiến trúc");
            // Service chỉ Add — người gọi lưu.
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var rows = await db.Notifications.OrderBy(n => n.RecipientUsername).ToListAsync();
            Assert.Equal(new[] { "admin", "teamdev" }, rows.Select(r => r.RecipientUsername).ToArray());
            Assert.All(rows, r =>
            {
                Assert.Equal(NotificationType.GateAwaitingApproval, r.Type);
                Assert.Equal("Cổng thanh toán", r.ProjectName);
                Assert.Equal(projectId, r.ProjectId);
                Assert.Equal(runId, r.WorkflowRunId);
                Assert.False(r.IsRead);
                Assert.Contains(projectId.ToString(), r.Link);
                Assert.Contains("Đề xuất kiến trúc", r.Message);
            });
        }
    }

    [Fact]
    public async Task NotifyGateOpened_AddsOnly_DoesNotSaveByItself()
    {
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var db = NewDb())
        {
            db.AppUsers.Add(new AppUser { Username = "teamdev", Role = UserRole.TeamDev, IsActive = true });
            db.Projects.Add(new Project { Id = projectId, Name = "P" });
            db.WorkflowRuns.Add(new WorkflowRun { Id = runId, ProjectId = projectId });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var run = await db.WorkflowRuns.FirstAsync(r => r.Id == runId);
            var svc = new NotificationService(db, FakePermissions.WithDeliveryAdvanceFor(UserRole.TeamDev), NullLogger<NotificationService>.Instance);
            await svc.NotifyGateOpenedAsync(run, "X");
            // KHÔNG SaveChanges ⇒ không có dòng nào được persist.
        }

        await using (var db = NewDb())
            Assert.Equal(0, await db.Notifications.CountAsync());
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

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }

    // Fake quyền: các role liệt kê được cấp DeliveryAdvance; role khác không có quyền nào.
    private sealed class FakePermissions : IPermissionService
    {
        private readonly HashSet<UserRole> _withDeliveryAdvance;

        private FakePermissions(HashSet<UserRole> roles) => _withDeliveryAdvance = roles;

        public static FakePermissions WithDeliveryAdvanceFor(params UserRole[] roles) => new(new HashSet<UserRole>(roles));

        public Task<IReadOnlySet<AppPermission>> GetGrantedAsync(UserRole role, CancellationToken cancellationToken = default)
        {
            var set = _withDeliveryAdvance.Contains(role)
                ? new HashSet<AppPermission> { AppPermission.DeliveryAdvance }
                : new HashSet<AppPermission>();
            return Task.FromResult<IReadOnlySet<AppPermission>>(set);
        }

        public Task<bool> HasPermissionAsync(ClaimsPrincipal user, AppPermission permission, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public void InvalidateCache() { }
    }
}
