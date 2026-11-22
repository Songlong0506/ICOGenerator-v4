using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Vai trò của người dùng đăng nhập (khác hẳn AgentRoleKey vốn dành cho các AI agent).
/// Cả ba vai trò đều cấu hình quyền qua popup Permissions (trang User Roles); riêng Admin luôn giữ
/// quyền quản lý ma trận quyền để không tự khóa mình (xem PermissionService.AdminLockedPermission).
/// Lưu ý: giá trị enum được lưu vào DB (RolePermission.Role, dạng chuỗi) nên KHÔNG đổi tên giá trị cũ.
/// Thứ tự đặc quyền KHÔNG suy ra từ giá trị số — quyền giữa các vai trò giao nhau chứ không lồng nhau.
/// [Description] = tên hiển thị trên UI (xem EnumDisplay.GetTitle); [SsoRoleClaim] = chuỗi role claim
/// tương ứng bên IdentityServer, là nguồn ánh xạ claim → vai trò (xem SsoRoleClaims / MapRole) thay cho
/// bảng "IdentityServer:RoleMappings" trong appsettings trước đây.
/// </summary>
public enum UserRole
{
    [Description("Administrator")]
    [SsoRoleClaim("HCP_CBO_API.CBO.ADMIN")]
    Admin = 0,
    [Description("Team Developer")]
    [SsoRoleClaim("HCP_CBO_API.CBO.TEAMDEV")]
    TeamDev = 1,
    [Description("User")]
    [SsoRoleClaim("HCP_CBO_API.CBO.USER")]
    User = 2
}
