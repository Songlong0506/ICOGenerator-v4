using ICOGenerator.Application.Notifications;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Notifications;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ICOGenerator.Tests.Notifications;

// Đọc/lưu tùy chọn thông báo của người dùng: validate email, ràng theo username.
public class NotificationPreferencesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public NotificationPreferencesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = NewDb();
        db.Database.EnsureCreated();
        db.AppUsers.Add(new AppUser { Username = "teamdev", Role = UserRole.TeamDev });
        db.SaveChanges();
    }

    [Fact]
    public async Task Update_SavesPreferences()
    {
        await using (var db = NewDb())
        {
            var result = await new UpdateNotificationPreferencesUseCase(db).ExecuteAsync("teamdev", new NotificationPreferencesVm
            {
                Email = "  me@bosch.com ",
                NotifyInApp = false,
                NotifyByEmail = true,
                NotifyOnGate = true,
                NotifyOnCompleted = false,
                NotifyOnFailed = true
            });
            Assert.Equal(UpdatePreferencesResult.Ok, result);
        }

        await using (var db = NewDb())
        {
            var u = await db.AppUsers.FirstAsync(x => x.Username == "teamdev");
            Assert.Equal("me@bosch.com", u.Email); // đã trim
            Assert.False(u.NotifyInApp);
            Assert.True(u.NotifyByEmail);
            Assert.False(u.NotifyOnCompleted);
        }
    }

    [Fact]
    public async Task Update_RejectsEmailOptInWithoutValidAddress()
    {
        await using var db = NewDb();
        var uc = new UpdateNotificationPreferencesUseCase(db);

        Assert.Equal(UpdatePreferencesResult.InvalidEmail,
            await uc.ExecuteAsync("teamdev", new NotificationPreferencesVm { NotifyByEmail = true, Email = "" }));

        Assert.Equal(UpdatePreferencesResult.InvalidEmail,
            await uc.ExecuteAsync("teamdev", new NotificationPreferencesVm { NotifyByEmail = true, Email = "not-an-email" }));

        // Không đổi gì (vẫn mặc định).
        var u = await db.AppUsers.FirstAsync(x => x.Username == "teamdev");
        Assert.False(u.NotifyByEmail);
        Assert.Null(u.Email);
    }

    [Fact]
    public async Task Update_RejectsInvalidEmail_EvenWhenNotOptedIn()
    {
        await using var db = NewDb();
        var result = await new UpdateNotificationPreferencesUseCase(db)
            .ExecuteAsync("teamdev", new NotificationPreferencesVm { NotifyByEmail = false, Email = "bad@@x" });
        Assert.Equal(UpdatePreferencesResult.InvalidEmail, result);
    }

    [Fact]
    public async Task Get_ReturnsSavedValues_AndEmailChannelFlag()
    {
        await using (var db = NewDb())
            await new UpdateNotificationPreferencesUseCase(db).ExecuteAsync("teamdev",
                new NotificationPreferencesVm { Email = "x@bosch.com", NotifyByEmail = true });

        await using (var db = NewDb())
        {
            var options = new NotificationOptions { Email = { Enabled = true, Host = "smtp", From = "a@b" } };
            var vm = await new GetNotificationPreferencesQuery(db, options).ExecuteAsync("teamdev");
            Assert.Equal("x@bosch.com", vm.Email);
            Assert.True(vm.NotifyByEmail);
            Assert.True(vm.EmailChannelConfigured); // admin đã cấu hình SMTP
        }

        await using (var db = NewDb())
        {
            var vm = await new GetNotificationPreferencesQuery(db, new NotificationOptions()).ExecuteAsync("teamdev");
            Assert.False(vm.EmailChannelConfigured); // chưa cấu hình
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
}
