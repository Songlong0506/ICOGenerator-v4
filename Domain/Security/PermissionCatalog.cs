using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain.Security;

/// <summary>
/// Một màn hình trong ứng dụng cùng nhóm quyền của nó. Dùng để render ma trận cấu hình
/// (Roles &amp; Permissions) và để lọc menu sidebar: menu chỉ hiện nếu role có <see cref="ViewPermission"/>.
/// </summary>
public sealed record PermissionScreen(string Title, AppPermission ViewPermission, IReadOnlyList<AppPermission> Permissions);

/// <summary>
/// Nguồn dữ liệu tĩnh mô tả các màn hình và quyền tương ứng. Đặt ở Domain để cả tầng Application
/// (use case dựng ma trận) lẫn View (lọc menu) cùng dùng một định nghĩa duy nhất.
/// </summary>
public static class PermissionCatalog
{
    public static IReadOnlyList<PermissionScreen> Screens { get; } = new[]
    {
        new PermissionScreen("Projects", AppPermission.ProjectsView,
            new[] { AppPermission.ProjectsView, AppPermission.ProjectsCreate, AppPermission.ProjectsEdit, AppPermission.ProjectsViewAll,
                AppPermission.ProjectsOpenRequirements, AppPermission.ProjectsOpenAgentDashboard, AppPermission.ProjectsOpenMockup,
                AppPermission.PocModelExperiment }),
        new PermissionScreen("Requirements", AppPermission.RequirementsView,
            new[] { AppPermission.RequirementsView, AppPermission.RequirementsManage }),
        // RequirementsDownloadPackage đứng ở nhóm Agents chứ không phải Requirements: endpoint
        // DownloadReviewPackage mà nó mở/khoá thuộc AgentDashboardController (cùng chỗ với DeliveryAdvance). Tên giá trị enum thì GIỮ
        // NGUYÊN — quyền đã lưu trong DB dạng chuỗi, đổi tên là mất quyền của mọi role đang có nó.
        new PermissionScreen("Agents", AppPermission.AgentsView,
            new[] { AppPermission.AgentsView, AppPermission.AgentsManage, AppPermission.DeliveryAdvance,
                AppPermission.RequirementsDownloadPackage }),
        new PermissionScreen("AI Models", AppPermission.ModelsView,
            new[] { AppPermission.ModelsView, AppPermission.ModelsCreate, AppPermission.ModelsEdit, AppPermission.ModelsDelete }),
        new PermissionScreen("Usage", AppPermission.UsageView,
            new[] { AppPermission.UsageView }),
        new PermissionScreen("Delivery Quality", AppPermission.QualityView,
            new[] { AppPermission.QualityView }),
        new PermissionScreen("Prompt Evals", AppPermission.EvalView,
            new[] { AppPermission.EvalView, AppPermission.EvalManage }),
        // Prompt Studio đã gộp vào màn hình Agents (danh sách prompt theo role của agent) — dùng chung
        // quyền Agents, không có ô riêng trong ma trận.
        new PermissionScreen("Settings", AppPermission.SettingsView,
            new[] { AppPermission.SettingsView, AppPermission.SettingsManage }),
        new PermissionScreen("Feedback", AppPermission.FeedbackView,
            new[] { AppPermission.FeedbackView, AppPermission.FeedbackManage }),
        new PermissionScreen("Roles & Permissions", AppPermission.AdministrationManageRoles,
            new[] { AppPermission.AdministrationManageRoles }),
        new PermissionScreen("User Roles", AppPermission.UserRolesView,
            new[] { AppPermission.UserRolesView, AppPermission.UserRolesManage }),
        // Hai tab của màn Logs — vẫn hai dòng riêng trong ma trận vì mỗi tab gác bằng quyền riêng
        // (System Logs có thể lộ exception/đường dẫn máy chủ nên không đi chung quyền với Audit Log).
        new PermissionScreen("Logs · Audit Log", AppPermission.AuditView,
            new[] { AppPermission.AuditView }),
        new PermissionScreen("Logs · System Logs", AppPermission.SystemLogView,
            new[] { AppPermission.SystemLogView }),
    };

    /// <summary>Mọi quyền đang được khai báo trong catalog (phẳng, không trùng).</summary>
    public static IReadOnlyList<AppPermission> AllPermissions { get; } =
        Screens.SelectMany(s => s.Permissions).Distinct().ToArray();
}
