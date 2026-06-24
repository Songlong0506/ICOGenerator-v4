using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Vai trò của người dùng đăng nhập (khác hẳn AgentRoleKey vốn dành cho các AI agent).
/// Admin luôn có toàn quyền (xem PermissionService); TeamDev và User được cấu hình quyền
/// linh hoạt qua màn hình Roles &amp; Permissions.
/// </summary>
public enum UserRole
{
    [Description("Administrator")]
    Admin = 0,
    [Description("Team Developer")]
    TeamDev = 1,
    [Description("User")]
    User = 2
}
