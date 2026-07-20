using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Vai trò của người dùng đăng nhập (khác hẳn AgentRoleKey vốn dành cho các AI agent).
/// SuperAdmin luôn có toàn quyền (xem PermissionService); Admin, TeamDev và User được cấu hình
/// quyền linh hoạt qua màn hình Roles &amp; Permissions.
/// Lưu ý: giá trị enum được lưu vào DB (AppUser.Role, RolePermission.Role) nên KHÔNG đổi số cũ;
/// SuperAdmin thêm sau với giá trị 3. Vì thế thứ tự đặc quyền KHÔNG suy ra từ giá trị số nữa —
/// dùng bảng ánh xạ tường minh khi cần so sánh (xem IdentityServerSettings.MapRole).
/// </summary>
public enum UserRole
{
    [Description("Administrator")]
    Admin = 0,
    [Description("Team Developer")]
    TeamDev = 1,
    [Description("User")]
    User = 2,
    [Description("Super Administrator")]
    SuperAdmin = 3
}
