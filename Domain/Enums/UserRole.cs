using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Vai trò của người dùng đăng nhập (khác hẳn AgentRoleKey vốn dành cho các AI agent).
/// SuperAdmin luôn có toàn quyền (xem PermissionService); Admin, TeamDev và User được cấu hình
/// quyền linh hoạt qua màn hình Roles &amp; Permissions.
/// Lưu ý: giá trị enum được lưu vào DB (AppUser.Role, RolePermission.Role) nên KHÔNG đổi số cũ;
/// SuperAdmin thêm sau với giá trị 3. Vì thế thứ tự đặc quyền KHÔNG suy ra từ giá trị số nữa —
/// dùng bảng ánh xạ tường minh khi cần so sánh (xem IdentityServerSettings.MapRole).
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
    User = 2,
    [Description("Super Administrator")]
    [SsoRoleClaim("HCP_CBO_API.CBO.SUPERADMIN")]
    SuperAdmin = 3
}
