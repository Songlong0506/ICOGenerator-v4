using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Cầu SSO → AppUser: TẬP vai trò lấy từ Claims (một người có thể giữ nhiều vai trò). User mới tạo với tập
// đó (rơi về vai trò mặc định nếu Claims không ánh xạ được), user cũ được đồng bộ: vai trò thừa bị thu hồi,
// vai trò mới được cấp thêm. Chạy trên AppDbContext Sqlite in-memory.
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
            "VUS5HC", "Vu Song Toan", "toan@bosch.com", new[] { UserRole.Admin });

        Assert.NotNull(user);
        Assert.Equal(new[] { UserRole.Admin }, RolesOf(user!));
        Assert.Equal("VUS5HC", user!.Username);
        // Được lưu xuống DB, không chỉ trong bộ nhớ.
        Assert.Equal(new[] { UserRole.Admin }, await PersistedRoles("VUS5HC"));
    }

    [Fact]
    public async Task NewUser_TakesAllRolesFromClaims()
    {
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync(
            "VUS5HC", "Vu Song Toan", null, new[] { UserRole.TeamDev, UserRole.User });

        Assert.NotNull(user);
        // Không gộp về một vai trò "cao nhất": quyền của hai vai trò giao nhau nên phải giữ cả hai.
        Assert.Equal(new[] { UserRole.TeamDev, UserRole.User }, RolesOf(user!));
        Assert.Equal(new[] { UserRole.TeamDev, UserRole.User }, await PersistedRoles("VUS5HC"));
    }

    [Fact]
    public async Task NewUser_NoRoleFromClaims_FallsBackToLowestPrivilegeRole()
    {
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync(
            "NEW1", "New User", null, Array.Empty<UserRole>());

        Assert.NotNull(user);
        Assert.Equal(new[] { UserRole.User }, RolesOf(user!));
    }

    [Fact]
    public async Task ExistingUser_RolesSyncedFromClaims()
    {
        await SeedUser("vus5hc", UserRole.User);
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync(
            "VUS5HC", "Vu Song Toan", null, new[] { UserRole.Admin });

        Assert.NotNull(user);
        Assert.Equal(new[] { UserRole.Admin }, RolesOf(user!));
        Assert.Equal(new[] { UserRole.Admin }, await PersistedRoles("vus5hc"));
    }

    [Fact]
    public async Task ExistingUser_ExtraRoleFromClaims_IsGranted_WithoutLosingCurrentOnes()
    {
        await SeedUser("vus5hc", UserRole.User);
        var sut = NewSut();

        await sut.ResolveOrProvisionAsync(
            "vus5hc", "Vu Song Toan", null, new[] { UserRole.User, UserRole.TeamDev });

        Assert.Equal(new[] { UserRole.TeamDev, UserRole.User }, await PersistedRoles("vus5hc"));
    }

    [Fact]
    public async Task ExistingUser_RoleWithdrawnOnIdp_IsRevoked()
    {
        await SeedUser("vus5hc", UserRole.User, UserRole.Admin);
        var sut = NewSut();

        // IdP chỉ còn phát USER ⇒ Admin bị thu hồi (IdentityServer là nguồn sự thật).
        await sut.ResolveOrProvisionAsync(
            "vus5hc", "Vu Song Toan", null, new[] { UserRole.User });

        Assert.Equal(new[] { UserRole.User }, await PersistedRoles("vus5hc"));
    }

    [Fact]
    public async Task ExistingUser_NoRoleFromClaims_KeepsCurrentRoles()
    {
        await SeedUser("admin", UserRole.Admin, UserRole.TeamDev);
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync(
            "admin", "Admin", null, Array.Empty<UserRole>());

        Assert.NotNull(user);
        // Mapping chưa cấu hình đủ không được phép hạ quyền người đang dùng.
        Assert.Equal(new[] { UserRole.Admin, UserRole.TeamDev }, await PersistedRoles("admin"));
    }

    [Fact]
    public async Task NewUser_TakesOrgUnitNameFromClaims()
    {
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync(
            "VUS5HC", "Vu Song Toan", null, new[] { UserRole.User }, orgUnitName: "HcP/ICO3");

        Assert.NotNull(user);
        Assert.Equal("HcP/ICO3", user!.OrgUnitName);
        await using var verify = NewDb();
        Assert.Equal("HcP/ICO3", (await verify.AppUsers.SingleAsync()).OrgUnitName);
    }

    [Fact]
    public async Task ExistingUser_OrgUnitNameSyncedFromClaims()
    {
        await SeedUser("vus5hc", UserRole.User);
        var sut = NewSut();

        await sut.ResolveOrProvisionAsync(
            "VUS5HC", "Vu Song Toan", null, Array.Empty<UserRole>(), orgUnitName: "HcP/ICO3");

        await using var verify = NewDb();
        Assert.Equal("HcP/ICO3", (await verify.AppUsers.SingleAsync()).OrgUnitName);
    }

    [Fact]
    public async Task ExistingUser_EmptyOrgUnitClaim_KeepsCurrentOrgUnit()
    {
        await SeedUser("vus5hc", "HcP/ICO3", UserRole.User);
        var sut = NewSut();

        await sut.ResolveOrProvisionAsync(
            "VUS5HC", "Vu Song Toan", null, Array.Empty<UserRole>(), orgUnitName: null);

        await using var verify = NewDb();
        Assert.Equal("HcP/ICO3", (await verify.AppUsers.SingleAsync()).OrgUnitName);
    }

    [Fact]
    public async Task EmptyUsername_ReturnsNull()
    {
        var sut = NewSut();

        Assert.Null(await sut.ResolveOrProvisionAsync(
            "   ", null, null, new[] { UserRole.Admin }));
    }

    private Task SeedUser(string username, params UserRole[] roles) => SeedUser(username, null, roles);

    private async Task SeedUser(string username, string? orgUnitName, params UserRole[] roles)
    {
        await using var db = NewDb();
        var user = new AppUser { Username = username, OrgUnitName = orgUnitName, DisplayName = username };
        foreach (var role in roles)
            user.Roles.Add(new AppUserRole { Role = role });
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
    }

    private static UserRole[] RolesOf(AppUser user) => user.Roles.Select(r => r.Role).OrderBy(r => r.ToString(), StringComparer.Ordinal).ToArray();

    private async Task<UserRole[]> PersistedRoles(string username)
    {
        await using var db = NewDb();
        var user = await db.AppUsers.Include(u => u.Roles).SingleAsync(u => u.Username == username);
        return RolesOf(user);
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
