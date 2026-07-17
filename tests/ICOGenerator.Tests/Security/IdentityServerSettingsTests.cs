using ICOGenerator.Application.Account;
using ICOGenerator.Domain.Enums;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Ánh xạ role claim của IdentityServer → UserRole của app: chọn vai trò cao nhất, không phân biệt hoa/thường,
// null khi không claim nào khớp mapping.
public class IdentityServerSettingsTests
{
    private static IdentityServerSettings WithMappings() => new()
    {
        RoleMappings =
        {
            ["HCP_CBO_API.CBO.ADMIN"] = UserRole.Admin,
            ["HCP_CBO_API.CBO.TEAMDEV"] = UserRole.TeamDev,
            ["HCP_CBO_API.CBO.USER"] = UserRole.User
        }
    };

    [Fact]
    public void MapRole_MapsKnownRole()
    {
        Assert.Equal(UserRole.Admin, WithMappings().MapRole(new[] { "HCP_CBO_API.CBO.ADMIN" }));
    }

    [Fact]
    public void MapRole_IsCaseInsensitive()
    {
        Assert.Equal(UserRole.Admin, WithMappings().MapRole(new[] { "hcp_cbo_api.cbo.admin" }));
    }

    [Fact]
    public void MapRole_TrimsWhitespace()
    {
        Assert.Equal(UserRole.TeamDev, WithMappings().MapRole(new[] { "  HCP_CBO_API.CBO.TEAMDEV  " }));
    }

    [Fact]
    public void MapRole_PicksHighestPrivilege_WhenMultipleRoles()
    {
        // User có cả USER lẫn ADMIN ⇒ nhận Admin (quyền cao nhất).
        var role = WithMappings().MapRole(new[] { "HCP_CBO_API.CBO.USER", "HCP_CBO_API.CBO.ADMIN" });
        Assert.Equal(UserRole.Admin, role);
    }

    [Fact]
    public void MapRole_ReturnsNull_WhenNoRoleMatches()
    {
        Assert.Null(WithMappings().MapRole(new[] { "SOME.OTHER.ROLE", "" }));
    }

    [Fact]
    public void MapRole_ReturnsNull_WhenNoRolesAtAll()
    {
        Assert.Null(WithMappings().MapRole(Array.Empty<string>()));
    }

    [Fact]
    public void MapRole_ReturnsNull_WhenMappingsEmpty()
    {
        Assert.Null(new IdentityServerSettings().MapRole(new[] { "HCP_CBO_API.CBO.ADMIN" }));
    }
}
