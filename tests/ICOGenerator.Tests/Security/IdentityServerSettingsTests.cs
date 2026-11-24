using ICOGenerator.Configuration;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Role claim của IdentityServer → role của phiên: CHÍNH tên role IS4, chỉ chuẩn hoá (trim + viết hoa), giữ
// MỌI role (quyền của chúng giao nhau nên không được gộp về role "cao nhất"), bỏ rỗng/trùng. Không còn ánh xạ
// qua enum ⇒ role IS4 mới tạo (không có trong UserRole) vẫn tới được PermissionService.
public class IdentityServerSettingsTests
{
    [Fact]
    public void MapRoles_KeepsUnknownRoles_Normalized()
    {
        Assert.Equal(
            new[] { "HCP_CBO_API.CBO.OPERATOR" },
            new IdentityServerSettings().MapRoles(new[] { "  hcp_cbo_api.cbo.Operator " }).ToArray());
    }

    [Fact]
    public void MapRoles_KeepsEveryRole_AndDeduplicates()
    {
        var roles = new IdentityServerSettings().MapRoles(new[]
        {
            "HCP_CBO_API.CBO.USER", "HCP_CBO_API.CBO.ADMIN", "hcp_cbo_api.cbo.admin", "", "   "
        });

        Assert.Equal(new[] { "HCP_CBO_API.CBO.ADMIN", "HCP_CBO_API.CBO.USER" },
            roles.OrderBy(r => r, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void MapRoles_ReturnsEmpty_WhenNoRolesAtAll()
    {
        Assert.Empty(new IdentityServerSettings().MapRoles(Array.Empty<string>()));
    }

    // Phiên cục bộ phát claim = role IS4 của LocalRole ⇒ cùng dạng đã chuẩn hoá như phiên SSO.
    [Fact]
    public void BuiltInRoles_MapToNormalizedIdentityServerNames()
    {
        Assert.Equal("HCP_CBO_API.CBO.ADMIN", UserRole.Admin.GetSsoRoleName());
        Assert.Equal(RoleNames.Admin, UserRole.Admin.GetSsoRoleName());
        foreach (var role in Enum.GetValues<UserRole>())
            Assert.Equal(RoleNames.Normalize(role.GetSsoRoleName()), role.GetSsoRoleName());
    }
}
