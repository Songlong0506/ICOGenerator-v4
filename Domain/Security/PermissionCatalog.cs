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
            new[] { AppPermission.ProjectsView, AppPermission.ProjectsCreate, AppPermission.ProjectsViewAll,
                AppPermission.ProjectsOpenRequirements, AppPermission.ProjectsOpenAgentDashboard, AppPermission.ProjectsOpenMockup }),
        new PermissionScreen("Requirements", "Requirements", AppPermission.RequirementsView,
            new[] { AppPermission.RequirementsView, AppPermission.RequirementsManage }),
        new PermissionScreen("Agents", "Agents", AppPermission.AgentsView,
            new[] { AppPermission.AgentsView, AppPermission.AgentsManage, AppPermission.DeliveryAdvance }),
        new PermissionScreen("Models", "AI Models", AppPermission.ModelsView,
            new[] { AppPermission.ModelsView, AppPermission.ModelsCreate, AppPermission.ModelsEdit, AppPermission.ModelsDelete }),
        new PermissionScreen("Usage", "Usage", AppPermission.UsageView,
            new[] { AppPermission.UsageView }),
        new PermissionScreen("Quality", "Delivery Quality", AppPermission.QualityView,
            new[] { AppPermission.QualityView }),
        new PermissionScreen("Evals", "Prompt Evals", AppPermission.EvalView,
            new[] { AppPermission.EvalView, AppPermission.EvalManage }),
        // Prompt Studio đã gộp vào màn hình Agents (danh sách prompt theo role của agent) — dùng chung
        // quyền Agents. Không còn màn hình riêng nên PromptView/PromptManage không xuất hiện trong ma trận.
        new PermissionScreen("Settings", "Settings", AppPermission.SettingsView,
            new[] { AppPermission.SettingsView, AppPermission.SettingsManage }),
        new PermissionScreen("Feedback", "Feedback", AppPermission.FeedbackView,
            new[] { AppPermission.FeedbackView, AppPermission.FeedbackManage }),
        new PermissionScreen("Roles", "Roles & Permissions", AppPermission.AdministrationManageRoles,
            new[] { AppPermission.AdministrationManageRoles }),
        new PermissionScreen("UserRoles", "User Roles", AppPermission.UserRolesView,
            new[] { AppPermission.UserRolesView, AppPermission.UserRolesManage }),
        new PermissionScreen("Audit", "Audit Log", AppPermission.AuditView,
            new[] { AppPermission.AuditView }),
    };

    /// <summary>Mọi quyền đang được khai báo trong catalog (phẳng, không trùng).</summary>
    public static IReadOnlyList<AppPermission> AllPermissions { get; } =
        Screens.SelectMany(s => s.Permissions).Distinct().ToArray();
}
