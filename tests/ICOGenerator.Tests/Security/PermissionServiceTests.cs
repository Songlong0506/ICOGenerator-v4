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

    // Không còn vai trò toàn quyền ngầm định ⇒ Admin phải luôn mở lại được ma trận quyền, kể cả khi bảng
    // trống hoặc Admin đã tự bỏ tick — nếu không thì chỉ còn đường sửa tay DB.
    [Fact]
    public async Task Admin_AlwaysKeepsManageRoles_EvenWithEmptyTable()
    {
        var service = new PermissionService(NewDb(), NewCache());

        Assert.Equal(new HashSet<AppPermission> { PermissionService.AdminLockedPermission },
            await service.GetGrantedAsync(UserRole.Admin));
        Assert.True(await service.HasPermissionAsync(Principal(UserRole.Admin), AppPermission.AdministrationManageRoles));
        Assert.False(await service.HasPermissionAsync(Principal(UserRole.TeamDev), AppPermission.AdministrationManageRoles));
    }

    [Fact]
    public async Task Admin_IsConfigurable_ReadsGrantedRowsNotImplicitAll()
    {
        await using (var db = NewDb())
        {
            db.RolePermissions.Add(new RolePermission { Role = UserRole.Admin, Permission = AppPermission.ProjectsView });
            await db.SaveChangesAsync();
        }

        var service = new PermissionService(NewDb(), NewCache());

        Assert.True(await service.HasPermissionAsync(Principal(UserRole.Admin), AppPermission.ProjectsView));
        // Không có implicit-all: quyền không được cấp thì không có.
        Assert.False(await service.HasPermissionAsync(Principal(UserRole.Admin), AppPermission.SettingsManage));
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

    // Lý do có multi-role: quyền của hai vai trò GIAO NHAU (mỗi bên có quyền bên kia không có). Người giữ
    // cả hai phải nhận HỢP quyền — cách cũ (gộp về vai trò "cao nhất") sẽ nuốt mất quyền của vai trò kia.
    [Fact]
    public async Task MultipleRoles_GetUnionOfPermissions_NotJustTheHighestRole()
    {
        await using (var db = NewDb())
        {
            db.RolePermissions.AddRange(
                new RolePermission { Role = UserRole.Admin, Permission = AppPermission.SettingsManage },
                new RolePermission { Role = UserRole.User, Permission = AppPermission.ProjectsView });
            await db.SaveChangesAsync();
        }

        var service = new PermissionService(NewDb(), NewCache());
        var both = Principal(UserRole.Admin, UserRole.User);

        Assert.True(await service.HasPermissionAsync(both, AppPermission.SettingsManage));
        Assert.True(await service.HasPermissionAsync(both, AppPermission.ProjectsView)); // chỉ vai trò User mới có
        Assert.False(await service.HasPermissionAsync(both, AppPermission.ProjectsCreate)); // không vai trò nào có
    }

    // Claim lạ (vai trò đã bỏ như SuperAdmin mà cookie cũ còn mang, hoặc IdP phát chuỗi khác) không được
    // làm hỏng lượt kiểm tra: bỏ qua nó và vẫn xét các vai trò hợp lệ còn lại.
    [Fact]
    public async Task UnknownRoleClaim_IsIgnored_ValidOnesStillCount()
    {
        await using (var db = NewDb())
        {
            db.RolePermissions.Add(new RolePermission { Role = UserRole.User, Permission = AppPermission.ProjectsView });
            await db.SaveChangesAsync();
        }

        var service = new PermissionService(NewDb(), NewCache());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "SuperAdmin"), new Claim(ClaimTypes.Role, UserRole.User.ToString()) }, "test"));

        Assert.True(await service.HasPermissionAsync(principal, AppPermission.ProjectsView));
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

    private static ClaimsPrincipal Principal(params UserRole[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r.ToString())), "test"));

    private static IMemoryCache NewCache() => new MemoryCache(new MemoryCacheOptions());

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();
}
