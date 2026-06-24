using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Một dòng cấp quyền: <see cref="Role"/> được phép thực hiện <see cref="Permission"/>.
/// Bảng này là nguồn dữ liệu CẤU HÌNH được — admin chỉnh qua màn hình Roles &amp; Permissions.
/// Role Admin KHÔNG cần dòng nào ở đây vì luôn được coi là có toàn quyền (xem PermissionService).
/// </summary>
public class RolePermission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public UserRole Role { get; set; }
    public AppPermission Permission { get; set; }
}
