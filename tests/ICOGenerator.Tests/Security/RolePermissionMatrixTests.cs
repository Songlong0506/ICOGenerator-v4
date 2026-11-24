using ICOGenerator.Application.Roles;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using ICOGenerator.Services.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Popup Roles & Permissions: cột lấy từ danh sách role IS4 (không cứng trong code), và lưu chỉ thay quyền
// của những role đang hiện trên popup.
public class RolePermissionMatrixTests : IDisposable
{
    private const string Operator = "HCP_CBO_API.CBO.OPERATOR";
    private const string Engineer = "HCP_CBO_API.CBO.ENGINEER";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public RolePermissionMatrixTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public void Columns_FollowIdentityServerRoles_InOrder_WithShortLabels()
    {
        var columns = GetRolePermissionMatrixQuery.BuildColumns(
            new[] { "HCP_CBO_API.SUPERADMIN", "HCP_CBO_API.CBO.ADMIN", "hcp_cbo_api.cbo.operator", Engineer },
            new Dictionary<string, HashSet<AppPermission>>
            {
                [Operator] = new() { AppPermission.ProjectsView }
            });

        Assert.Equal(new[] { "HCP_CBO_API.SUPERADMIN", RoleNames.Admin, Operator, Engineer }, columns.Select(c => c.Role));
        Assert.Equal(new[] { "SUPERADMIN", "ADMIN", "OPERATOR", "ENGINEER" }, columns.Select(c => c.Title));
        Assert.All(columns, c => Assert.False(c.MissingFromIdentityServer));
        Assert.True(columns[2].Has(AppPermission.ProjectsView));
        Assert.False(columns[3].Has(AppPermission.ProjectsView));
    }

    // Role còn quyền trong DB nhưng IS4 không trả về (đã xoá bên IS4, hoặc IS4 lỗi ⇒ null) vẫn phải hiện để
    // admin thấy và thu hồi được; Admin luôn có cột vì giữ ô khóa.
    [Fact]
    public void Columns_KeepDbOnlyRoles_AndAlwaysAdmin()
    {
        var granted = new Dictionary<string, HashSet<AppPermission>>
        {
            ["HCP_CBO_API.CBO.TEAMDEV"] = new() { AppPermission.UsageView }
        };

        var withIs4 = GetRolePermissionMatrixQuery.BuildColumns(new[] { Operator }, granted);
        Assert.Equal(new[] { Operator, "HCP_CBO_API.CBO.TEAMDEV", RoleNames.Admin }, withIs4.Select(c => c.Role));
        Assert.Equal(new[] { false, true, true }, withIs4.Select(c => c.MissingFromIdentityServer));

        var withoutIs4 = GetRolePermissionMatrixQuery.BuildColumns(null, granted);
        Assert.Equal(new[] { "HCP_CBO_API.CBO.TEAMDEV", RoleNames.Admin }, withoutIs4.Select(c => c.Role));
        Assert.All(withoutIs4, c => Assert.False(c.MissingFromIdentityServer)); // không biết thì không kết luận
        Assert.True(withoutIs4.Single(c => c.Role == RoleNames.Admin).IsLocked(PermissionService.AdminLockedPermission));
    }

    [Fact]
    public async Task Update_ReplacesOnlyRolesOnThePopup_AndAcceptsNewIdentityServerRoles()
    {
        await using (var db = NewDb())
        {
            db.RolePermissions.AddRange(
                new RolePermission { Role = Engineer, Permission = AppPermission.ProjectsView },
                new RolePermission { Role = "HCP_CBO_API.CBO.TEAMDEV", Permission = AppPermission.UsageView });
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var useCase = new UpdateRolePermissionsUseCase(db, new PermissionService(db, NewCache()), new NullAuditLogger());
            await useCase.ExecuteAsync(
                roles: new[] { Operator, Engineer },
                granted: new[]
                {
                    "hcp_cbo_api.cbo.operator:ProjectsView",
                    $"{Operator}:ProjectsCreate",
                    $"{Operator}:NotAPermission",
                    "HCP_CBO_API.CBO.TEAMDEV:SettingsManage", // role không có trên popup ⇒ bỏ qua
                    "garbage"
                });
        }

        await using var check = NewDb();
        var rows = await check.RolePermissions.Select(x => new { x.Role, x.Permission }).ToListAsync();

        Assert.Equal(
            new[] { (Operator, AppPermission.ProjectsCreate), (Operator, AppPermission.ProjectsView) },
            rows.Where(r => r.Role == Operator).Select(r => (r.Role, r.Permission)).OrderBy(r => r.Permission.ToString()));
        Assert.DoesNotContain(rows, r => r.Role == Engineer); // bỏ tick hết ⇒ không còn dòng nào
        Assert.Equal(new[] { AppPermission.UsageView },
            rows.Where(r => r.Role == "HCP_CBO_API.CBO.TEAMDEV").Select(r => r.Permission)); // không bị đụng

        var service = new PermissionService(check, NewCache());
        Assert.Contains(AppPermission.ProjectsCreate, await service.GetGrantedAsync(Operator));
    }

    private static IMemoryCache NewCache() => new MemoryCache(new MemoryCacheOptions());

    private AppDbContext NewDb() => new(_options, new PassthroughApiKeyProtector());

    public void Dispose() => _connection.Dispose();

    private sealed class NullAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditCategory category, AuditAction action, string entityId, string summary,
            object? before = null, object? after = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
