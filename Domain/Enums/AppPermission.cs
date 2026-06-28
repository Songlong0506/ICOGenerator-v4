using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Danh mục quyền ở mức HÀNH ĐỘNG cho từng màn hình. Lưu xuống DB dạng chuỗi (tên enum) trong
/// bảng RolePermission, nên ĐỪNG đổi tên các giá trị đã seed (sẽ làm "mồ côi" quyền cũ).
/// PermissionCatalog gom các quyền này theo màn hình để render ma trận cấu hình và lọc menu.
/// </summary>
public enum AppPermission
{
    [Description("Xem danh sách dự án")]
    ProjectsView,
    [Description("Tạo dự án mới")]
    ProjectsCreate,

    [Description("Xem workspace yêu cầu (Requirements)")]
    RequirementsView,
    [Description("Thao tác workflow yêu cầu (chat BA, duyệt, chạy lại...)")]
    RequirementsManage,

    [Description("Xem cấu hình Agents")]
    AgentsView,
    [Description("Chỉnh sửa cấu hình Agent")]
    AgentsManage,
    [Description("Duyệt & đẩy các bước delivery (Architecture, code, test...) trên Agent Dashboard")]
    DeliveryAdvance,

    [Description("Xem danh sách AI Models")]
    ModelsView,
    [Description("Thêm AI Model")]
    ModelsCreate,
    [Description("Sửa AI Model")]
    ModelsEdit,
    [Description("Xóa AI Model")]
    ModelsDelete,

    [Description("Xem báo cáo Usage")]
    UsageView,

    [Description("Xem Settings")]
    SettingsView,
    [Description("Lưu thay đổi Settings")]
    SettingsManage,

    [Description("Quản trị Roles & Permissions")]
    AdministrationManageRoles
}
