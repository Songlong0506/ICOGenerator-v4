using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Danh mục quyền ở mức HÀNH ĐỘNG cho từng màn hình. Lưu xuống DB dạng chuỗi (tên enum) trong
/// bảng RolePermission, nên ĐỪNG đổi tên các giá trị đã seed (sẽ làm "mồ côi" quyền cũ).
/// PermissionCatalog gom các quyền này theo màn hình để render ma trận cấu hình và lọc menu.
/// </summary>
public enum AppPermission
{
    [Description("Read")]
    ProjectsView,
    [Description("Create")]
    ProjectsCreate,
    [Description("View all projects (not just own)")]
    ProjectsViewAll,
    [Description("Show Requirements button on project list")]
    ProjectsOpenRequirements,
    [Description("Show Agent Dashboard button on project list")]
    ProjectsOpenAgentDashboard,
    [Description("Show Mockup button on project list")]
    ProjectsOpenMockup,

    [Description("Read")]
    RequirementsView,
    [Description("Manage requirements workflow (chat BA, approve, re-run...)")]
    RequirementsManage,

    [Description("Read")]
    AgentsView,
    [Description("Update")]
    AgentsManage,
    [Description("Approve & advance delivery steps (Architecture, code, test...)")]
    DeliveryAdvance,

    [Description("Read")]
    ModelsView,
    [Description("Create")]
    ModelsCreate,
    [Description("Update")]
    ModelsEdit,
    [Description("Delete")]
    ModelsDelete,

    [Description("Read")]
    UsageView,

    [Description("Read")]
    QualityView,

    [Description("Read")]
    EvalView,
    [Description("Manage scenarios & run evals (consumes real tokens)")]
    EvalManage,

    // Đã nghỉ hưu: Prompt Studio gộp vào màn hình Agents, quyền đi theo AgentsView/AgentsManage.
    // GIỮ giá trị (quyền lưu DB dạng chuỗi tên enum) để không "mồ côi" bản ghi cũ; không còn code nào đọc.
    [Description("(Merged into Agents) View prompt")]
    PromptView,
    [Description("(Merged into Agents) Edit/activate/rollback prompt version")]
    PromptManage,

    [Description("Read")]
    SettingsView,
    [Description("Update")]
    SettingsManage,

    [Description("Submit & view own feedback")]
    FeedbackView,
    [Description("View all feedback & update status (triage)")]
    FeedbackManage,

    [Description("Manage Roles & Permissions")]
    AdministrationManageRoles,

    [Description("Read")]
    UserRolesView,
    [Description("Assign / revoke user permissions (IdentityServer)")]
    UserRolesManage,

    [Description("Read")]
    AuditView
}
