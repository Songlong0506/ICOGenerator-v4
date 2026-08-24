using System.Reflection;
using ICOGenerator.Controllers;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using ICOGenerator.Services.Security;
using Xunit;

namespace ICOGenerator.Tests.Security;

// Một quyền chỉ có ích khi admin CẤP được nó. Ma trận Roles & Permissions render từ
// PermissionCatalog.Screens, nên một giá trị AppPermission không được khai báo vào catalog sẽ không có ô
// nào để tick: endpoint gắn quyền đó bị khoá vĩnh viễn với mọi role trừ SuperAdmin (implicit-all), mà
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

    // Nút "Tải trọn gói cho AI" là đường đem cả chuỗi tài liệu dự án ra ngoài hệ thống thành một file.
    // Quyền riêng cho nó (chồng lên RequirementsView của controller) là chốt cố ý: được xem trang
    // Requirements không đương nhiên được xuất dữ liệu.
    [Fact]
    public void DownloadReviewPackage_RequiresItsOwnPermission()
    {
        var action = typeof(RequirementsController)
            .GetMethod(nameof(RequirementsController.DownloadReviewPackage), BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(action);

        var required = action!.GetCustomAttributes<RequirePermissionAttribute>()
            .SelectMany(x => (AppPermission[])x.Arguments![0])
            .ToList();

        Assert.Contains(AppPermission.RequirementsDownloadPackage, required);
    }
}
