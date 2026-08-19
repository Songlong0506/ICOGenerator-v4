using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Cầu SSO → AppUser: quy danh tính IdP về một AppUser (tra theo username, tạo mới nếu chưa có, đồng bộ đơn
// vị tổ chức). KHÔNG đụng tới vai trò — vai trò không được lưu ở đâu trong DB, nó đi thẳng từ role claim vào
// claim của phiên (ánh xạ claim → UserRole test ở IdentityServerSettingsTests).
// Chạy trên AppDbContext Sqlite in-memory.
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
    public async Task NewUser_IsCreatedFromClaims()
    {
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync("VUS5HC", "Vu Song Toan", "toan@bosch.com");

        Assert.NotNull(user);
        Assert.Equal("VUS5HC", user!.Username);
        Assert.Equal("Vu Song Toan", user.DisplayName);
        Assert.Equal("toan@bosch.com", user.Email);

        // Được lưu xuống DB, không chỉ trong bộ nhớ.
        await using var verify = NewDb();
        var persisted = await verify.AppUsers.SingleAsync();
        Assert.Equal("VUS5HC", persisted.Username);
    }

    [Fact]
    public async Task NewUser_WithoutDisplayName_FallsBackToUsername()
    {
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync("VUS5HC", "   ", null);

        Assert.NotNull(user);
        Assert.Equal("VUS5HC", user!.DisplayName);
    }

    [Fact]
    public async Task ExistingUser_IsMatchedCaseInsensitively_AndNotDuplicated()
    {
        await SeedUser("vus5hc");
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync("VUS5HC", "Vu Song Toan", null);

        Assert.NotNull(user);
        Assert.Equal("vus5hc", user!.Username);
        await using var verify = NewDb();
        Assert.Equal(1, await verify.AppUsers.CountAsync());
    }

    [Fact]
    public async Task NewUser_TakesOrgUnitNameFromClaims()
    {
        var sut = NewSut();

        var user = await sut.ResolveOrProvisionAsync("VUS5HC", "Vu Song Toan", null, orgUnitName: "HcP/ICO3");

        Assert.NotNull(user);
        Assert.Equal("HcP/ICO3", user!.OrgUnitName);
        await using var verify = NewDb();
        Assert.Equal("HcP/ICO3", (await verify.AppUsers.SingleAsync()).OrgUnitName);
    }

    [Fact]
    public async Task ExistingUser_OrgUnitNameSyncedFromClaims()
    {
        await SeedUser("vus5hc");
        var sut = NewSut();

        await sut.ResolveOrProvisionAsync("VUS5HC", "Vu Song Toan", null, orgUnitName: "HcP/ICO3");

        await using var verify = NewDb();
        Assert.Equal("HcP/ICO3", (await verify.AppUsers.SingleAsync()).OrgUnitName);
    }

    [Fact]
    public async Task ExistingUser_EmptyOrgUnitClaim_KeepsCurrentOrgUnit()
    {
        await SeedUser("vus5hc", "HcP/ICO3");
        var sut = NewSut();

        await sut.ResolveOrProvisionAsync("VUS5HC", "Vu Song Toan", null, orgUnitName: null);

        await using var verify = NewDb();
        Assert.Equal("HcP/ICO3", (await verify.AppUsers.SingleAsync()).OrgUnitName);
    }

    [Fact]
    public async Task EmptyUsername_ReturnsNull()
    {
        var sut = NewSut();

        Assert.Null(await sut.ResolveOrProvisionAsync("   ", null, null));
    }

    private Task SeedUser(string username) => SeedUser(username, null);

    private async Task SeedUser(string username, string? orgUnitName)
    {
        await using var db = NewDb();
        db.AppUsers.Add(new AppUser { Username = username, OrgUnitName = orgUnitName, DisplayName = username });
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
    }
}
