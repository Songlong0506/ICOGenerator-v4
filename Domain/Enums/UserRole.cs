using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Các vai trò DỰNG SẴN của người dùng đăng nhập (khác hẳn AgentRoleKey vốn dành cho các AI agent). Phân
/// quyền thật KHÔNG khóa theo enum này mà theo tên role IdentityServer (xem <see cref="Security.RoleNames"/>):
/// ma trận Roles &amp; Permissions lấy cột từ danh sách role IS4. Enum chỉ còn dùng cho: (1) giá trị hợp lệ
/// của <c>IdentityServer:LocalRole</c> — phiên cục bộ nhận claim là role IS4 tương ứng; (2) xác định role
/// Admin luôn giữ quyền quản lý ma trận (xem PermissionService.AdminLockedPermission); (3) seed quyền mặc định.
/// Tên giá trị là chuỗi người dùng gõ ở <c>LocalRole</c> nên KHÔNG đổi tên giá trị cũ.
/// [Description] = tên hiển thị trên UI (xem EnumDisplay.GetTitle); [SsoRoleClaim] = tên role bên IdentityServer.
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
