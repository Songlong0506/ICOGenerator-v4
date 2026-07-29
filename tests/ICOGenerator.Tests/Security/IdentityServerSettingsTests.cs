using ICOGenerator.Configuration;
using ICOGenerator.Domain.Enums;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Ánh xạ role claim của IdentityServer → tập UserRole của app: giữ MỌI vai trò khớp (quyền của chúng giao
// nhau nên không được gộp về vai trò "cao nhất"), không phân biệt hoa/thường, rỗng khi không claim nào khớp.
// Ánh xạ lấy từ attribute [SsoRoleClaim] trên enum UserRole nên MapRoles chạy được ngay với instance mặc
// định (không cần cấu hình RoleMappings).
public class IdentityServerSettingsTests
{
    [Fact]
    public void MapRoles_MapsKnownRole()
    {
        Assert.Equal(
            new[] { UserRole.Admin },
            new IdentityServerSettings().MapRoles(new[] { "HCP_CBO_API.CBO.ADMIN" }).ToArray());
    }

    [Fact]
    public void MapRoles_IsCaseInsensitive()
    {
        Assert.Equal(
            new[] { UserRole.Admin },
            new IdentityServerSettings().MapRoles(new[] { "hcp_cbo_api.cbo.admin" }).ToArray());
    }

    [Fact]
    public void MapRoles_TrimsWhitespace()
    {
        Assert.Equal(
            new[] { UserRole.TeamDev },
            new IdentityServerSettings().MapRoles(new[] { "  HCP_CBO_API.CBO.TEAMDEV  " }).ToArray());
    }

    [Fact]
    public void MapRoles_KeepsEveryMatchedRole()
    {
        // User có cả USER lẫn ADMIN ⇒ giữ CẢ HAI. Trước đây chỉ giữ Admin, làm mất những quyền mà
        // riêng vai trò User mới được cấp.
        var roles = new IdentityServerSettings().MapRoles(new[] { "HCP_CBO_API.CBO.USER", "HCP_CBO_API.CBO.ADMIN" });

        Assert.Equal(new[] { UserRole.Admin, UserRole.User }, roles.OrderBy(r => r.ToString(), StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void MapRoles_DeduplicatesRepeatedClaims()
    {
        var roles = new IdentityServerSettings().MapRoles(new[] { "HCP_CBO_API.CBO.ADMIN", "hcp_cbo_api.cbo.admin" });

        Assert.Equal(new[] { UserRole.Admin }, roles.ToArray());
    }

    [Fact]
    public void MapRoles_IgnoresUnknownClaims_ButKeepsKnownOnes()
    {
        var roles = new IdentityServerSettings().MapRoles(new[] { "SOME.OTHER.ROLE", "HCP_CBO_API.CBO.TEAMDEV" });

        Assert.Equal(new[] { UserRole.TeamDev }, roles.ToArray());
    }

    [Fact]
    public void MapRoles_ReturnsEmpty_WhenNoRoleMatches()
    {
        Assert.Empty(new IdentityServerSettings().MapRoles(new[] { "SOME.OTHER.ROLE", "" }));
    }

    [Fact]
    public void MapRoles_ReturnsEmpty_WhenNoRolesAtAll()
    {
        Assert.Empty(new IdentityServerSettings().MapRoles(Array.Empty<string>()));
    }
}
