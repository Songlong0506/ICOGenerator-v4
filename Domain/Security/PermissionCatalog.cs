using System.ComponentModel;
using System.Reflection;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain.Security;

/// <summary>
/// Một màn hình trong ứng dụng cùng nhóm quyền của nó. Dùng để render ma trận cấu hình
/// (Roles &amp; Permissions) và để lọc menu sidebar: menu chỉ hiện nếu role có <see cref="ViewPermission"/>.
/// </summary>
public sealed record PermissionScreen(string Key, string Title, AppPermission ViewPermission, IReadOnlyList<AppPermission> Permissions);

/// <summary>
/// Nguồn dữ liệu tĩnh mô tả các màn hình và quyền tương ứng. Đặt ở Domain để cả tầng Application
/// (use case dựng ma trận) lẫn View (lọc menu) cùng dùng một định nghĩa duy nhất.
/// </summary>
public static class PermissionCatalog
{
    public static IReadOnlyList<PermissionScreen> Screens { get; } = new[]
    {
        new PermissionScreen("Projects", "Projects", AppPermission.ProjectsView,
            new[] { AppPermission.ProjectsView, AppPermission.ProjectsCreate }),
        new PermissionScreen("Requirements", "Requirements", AppPermission.RequirementsView,
            new[] { AppPermission.RequirementsView, AppPermission.RequirementsManage }),
        new PermissionScreen("Agents", "Agents", AppPermission.AgentsView,
            new[] { AppPermission.AgentsView, AppPermission.AgentsManage }),
        new PermissionScreen("Models", "AI Models", AppPermission.ModelsView,
            new[] { AppPermission.ModelsView, AppPermission.ModelsCreate, AppPermission.ModelsEdit, AppPermission.ModelsDelete }),
        new PermissionScreen("Usage", "Usage", AppPermission.UsageView,
            new[] { AppPermission.UsageView }),
        new PermissionScreen("Settings", "Settings", AppPermission.SettingsView,
            new[] { AppPermission.SettingsView, AppPermission.SettingsManage }),
        new PermissionScreen("Roles", "Roles & Permissions", AppPermission.AdministrationManageRoles,
            new[] { AppPermission.AdministrationManageRoles }),
    };

    /// <summary>Mọi quyền đang được khai báo trong catalog (phẳng, không trùng).</summary>
    public static IReadOnlyList<AppPermission> AllPermissions { get; } =
        Screens.SelectMany(s => s.Permissions).Distinct().ToArray();

    /// <summary>Nhãn tiếng Việt của quyền lấy từ [Description], fallback về tên enum.</summary>
    public static string GetLabel(this AppPermission permission)
    {
        var member = typeof(AppPermission).GetMember(permission.ToString()).FirstOrDefault();
        var description = member?.GetCustomAttribute<DescriptionAttribute>()?.Description;
        return string.IsNullOrWhiteSpace(description) ? permission.ToString() : description;
    }
}
