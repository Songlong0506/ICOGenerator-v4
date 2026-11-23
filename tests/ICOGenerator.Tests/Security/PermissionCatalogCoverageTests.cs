using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Một quyền chỉ có ích khi admin CẤP được nó. Ma trận Roles & Permissions render từ
// PermissionCatalog.Screens, nên một giá trị AppPermission không được khai báo vào catalog sẽ không có ô
// nào để tick: endpoint gắn quyền đó bị khoá vĩnh viễn với mọi role, mà
// không có thông báo lỗi nào. Đây là thứ VẮNG MẶT — build vẫn xanh — nên chốt bằng test.
public class PermissionCatalogCoverageTests
{
    [Fact]
    public void EveryPermission_IsDeclaredInCatalog()
    {
        var missing = Enum.GetValues<AppPermission>()
            .Except(PermissionCatalog.AllPermissions)
            .ToList();

        Assert.True(missing.Count == 0,
            $"Quyền {string.Join(", ", missing)} chưa được khai báo trong PermissionCatalog.Screens — " +
            "sẽ không có ô nào trong ma trận Roles & Permissions để admin cấp.");
    }
}
