using ICOGenerator.Application.Account;
using ICOGenerator.Domain.Enums;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Ánh xạ role claim của IdentityServer → UserRole của app: chọn vai trò cao nhất, không phân biệt hoa/thường,
// null khi không claim nào khớp. Ánh xạ lấy từ attribute [SsoRoleClaim] trên enum UserRole nên MapRole chạy
// được ngay với instance mặc định (không cần cấu hình RoleMappings).
public class IdentityServerSettingsTests
{
    [Fact]
    public void MapRole_MapsKnownRole()
    {
        Assert.Equal(UserRole.Admin, new IdentityServerSettings().MapRole(new[] { "HCP_CBO_API.CBO.ADMIN" }));
    }

    [Fact]
    public void MapRole_IsCaseInsensitive()
    {
        Assert.Equal(UserRole.Admin, new IdentityServerSettings().MapRole(new[] { "hcp_cbo_api.cbo.admin" }));
    }

    [Fact]
    public void MapRole_TrimsWhitespace()
    {
        Assert.Equal(UserRole.TeamDev, new IdentityServerSettings().MapRole(new[] { "  HCP_CBO_API.CBO.TEAMDEV  " }));
    }

    [Fact]
    public void MapRole_PicksHighestPrivilege_WhenMultipleRoles()
    {
        // User có cả USER lẫn ADMIN ⇒ nhận Admin (quyền cao nhất).
        var role = new IdentityServerSettings().MapRole(new[] { "HCP_CBO_API.CBO.USER", "HCP_CBO_API.CBO.ADMIN" });
        Assert.Equal(UserRole.Admin, role);
    }

    [Fact]
    public void MapRole_SuperAdmin_OutranksAdmin()
    {
        // SuperAdmin có giá trị enum lớn hơn Admin nhưng phải thắng nhờ thứ hạng đặc quyền tường minh.
        var role = new IdentityServerSettings().MapRole(new[] { "HCP_CBO_API.CBO.ADMIN", "HCP_CBO_API.CBO.SUPERADMIN" });
        Assert.Equal(UserRole.SuperAdmin, role);
    }

    [Fact]
    public void MapRole_ReturnsNull_WhenNoRoleMatches()
    {
        Assert.Null(new IdentityServerSettings().MapRole(new[] { "SOME.OTHER.ROLE", "" }));
    }

    [Fact]
    public void MapRole_ReturnsNull_WhenNoRolesAtAll()
    {
        Assert.Null(new IdentityServerSettings().MapRole(Array.Empty<string>()));
    }
}
