using System.Security.Claims;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Phân quyền chạy trên AppDbContext thật (Sqlite in-memory) + MemoryCache thật để bám sát runtime.
public class PermissionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PermissionServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Admin_AlwaysHasAllPermissions_EvenWithEmptyTable()
    {
        var service = new PermissionService(NewDb(), NewCache());

        var granted = await service.GetGrantedAsync(UserRole.Admin);

        Assert.Equal(Enum.GetValues<AppPermission>().ToHashSet(), granted);
        Assert.True(await service.HasPermissionAsync(Principal(UserRole.Admin), AppPermission.AdministrationManageRoles));
    }

    [Fact]
    public async Task NonAdmin_GetsExactlyTheGrantedRows()
    {
        await using (var db = NewDb())
        {
            db.RolePermissions.Add(new RolePermission { Role = UserRole.User, Permission = AppPermission.ProjectsView });
            await db.SaveChangesAsync();
        }

        var service = new PermissionService(NewDb(), NewCache());

        Assert.True(await service.HasPermissionAsync(Principal(UserRole.User), AppPermission.ProjectsView));
        Assert.False(await service.HasPermissionAsync(Principal(UserRole.User), AppPermission.ProjectsCreate));
    }

    [Fact]
    public async Task Unauthenticated_HasNoPermission()
    {
        var service = new PermissionService(NewDb(), NewCache());
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity()); // không có auth type => chưa đăng nhập

        Assert.False(await service.HasPermissionAsync(anonymous, AppPermission.ProjectsView));
    }

    [Fact]
    public async Task InvalidateCache_MakesNextReadSeeDbChange()
    {
        // Chia sẻ cùng một cache để chứng minh hành vi cache + invalidate (không phải do tạo cache mới).
        var cache = NewCache();

        var warmUp = new PermissionService(NewDb(), cache);
        Assert.False(await warmUp.HasPermissionAsync(Principal(UserRole.TeamDev), AppPermission.UsageView));

        await using (var db = NewDb())
        {
            db.RolePermissions.Add(new RolePermission { Role = UserRole.TeamDev, Permission = AppPermission.UsageView });
            await db.SaveChangesAsync();
        }

        var stale = new PermissionService(NewDb(), cache);
        Assert.False(await stale.HasPermissionAsync(Principal(UserRole.TeamDev), AppPermission.UsageView)); // vẫn đọc cache cũ

        stale.InvalidateCache();
        Assert.True(await stale.HasPermissionAsync(Principal(UserRole.TeamDev), AppPermission.UsageView)); // đọc lại từ DB
    }

    private static ClaimsPrincipal Principal(UserRole role) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, role.ToString()) }, "test"));

    private static IMemoryCache NewCache() => new MemoryCache(new MemoryCacheOptions());

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class PassthroughApiKeyProtector : IApiKeyProtector
    {
        public string Protect(string? plainText) => plainText ?? string.Empty;
        public string Unprotect(string? storedValue) => storedValue ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
