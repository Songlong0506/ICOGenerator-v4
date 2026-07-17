using ICOGenerator.Application.Account;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Cầu SSO → AppUser: vai trò lấy từ Claims. User mới tạo với vai trò đó (rơi về DefaultRole nếu Claims
// không ánh xạ được), user cũ được đồng bộ vai trò từ Claims. Chạy trên AppDbContext Sqlite in-memory.
public class SsoUserProvisionerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public SsoUserProvisionerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task NewUser_TakesRoleFromClaims()
    {
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync(
            "VUS5HC", "Vu Song Toan", "toan@bosch.com", UserRole.Admin, UserRole.User);

        Assert.NotNull(user);
        Assert.Equal(UserRole.Admin, user!.Role);
        Assert.Equal("VUS5HC", user.Username);
        // Được lưu xuống DB, không chỉ trong bộ nhớ.
        await using var verify = NewDb();
        Assert.Equal(UserRole.Admin, (await verify.AppUsers.SingleAsync()).Role);
    }

    [Fact]
    public async Task NewUser_NoRoleFromClaims_FallsBackToDefaultRole()
    {
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync(
            "NEW1", "New User", null, roleFromClaims: null, defaultRole: UserRole.TeamDev);

        Assert.NotNull(user);
        Assert.Equal(UserRole.TeamDev, user!.Role);
    }

    [Fact]
    public async Task ExistingUser_RoleSyncedFromClaims()
    {
        await SeedUser("vus5hc", UserRole.User);
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync(
            "VUS5HC", "Vu Song Toan", null, UserRole.Admin, UserRole.User);

        Assert.NotNull(user);
        Assert.Equal(UserRole.Admin, user!.Role);
        await using var verify = NewDb();
        Assert.Equal(UserRole.Admin, (await verify.AppUsers.SingleAsync()).Role);
    }

    [Fact]
    public async Task ExistingUser_NoRoleFromClaims_KeepsCurrentRole()
    {
        await SeedUser("admin", UserRole.Admin);
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync(
            "admin", "Admin", null, roleFromClaims: null, defaultRole: UserRole.User);

        Assert.NotNull(user);
        Assert.Equal(UserRole.Admin, user!.Role);
    }

    [Fact]
    public async Task ExistingInactiveUser_ReturnsNull_DoesNotResurrect()
    {
        await SeedUser("blocked", UserRole.User, isActive: false);
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync(
            "blocked", "Blocked", null, UserRole.Admin, UserRole.User);

        Assert.Null(user);
        // Vai trò không bị thay đổi cho user đã khóa.
        await using var verify = NewDb();
        Assert.Equal(UserRole.User, (await verify.AppUsers.SingleAsync()).Role);
    }

    [Fact]
    public async Task EmptyUsername_ReturnsNull()
    {
        var sut = NewSut();

        Assert.Null(await sut.ResolveOrProvisionAsync(
            "   ", null, null, UserRole.Admin, UserRole.User));
    }

    private async Task SeedUser(string username, UserRole role, bool isActive = true)
    {
        await using var db = NewDb();
        var user = new AppUser { Username = username, Role = role, IsActive = isActive, DisplayName = username };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
    }

    private SsoUserProvisioner NewSut() =>
        new(NewDb(), NullLogger<SsoUserProvisioner>.Instance);

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
