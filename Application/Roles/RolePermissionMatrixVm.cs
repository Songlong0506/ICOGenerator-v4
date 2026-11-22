using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using ICOGenerator.Services.Security;

namespace ICOGenerator.Application.Roles;

/// <summary>
/// Dữ liệu cho màn hình cấu hình Roles &amp; Permissions: các màn hình (hàng) × các role (cột).
/// Ô Admin × <see cref="PermissionService.AdminLockedPermission"/> bị khóa (luôn có) để Admin không tự khóa mình.
/// </summary>
public class RolePermissionMatrixVm
{
    public IReadOnlyList<PermissionScreen> Screens { get; init; } = PermissionCatalog.Screens;
    public IReadOnlyList<RolePermissionColumn> Columns { get; init; } = Array.Empty<RolePermissionColumn>();
}

public class RolePermissionColumn
{
    public UserRole Role { get; init; }
    public string Title { get; init; } = string.Empty;

    public HashSet<AppPermission> Granted { get; init; } = new();

    /// <summary>Ô luôn được cấp, không cho sửa: hiển thị dấu khóa thay cho checkbox.</summary>
    public bool IsLocked(AppPermission permission) =>
        Role == UserRole.Admin && permission == PermissionService.AdminLockedPermission;

    public bool Has(AppPermission permission) => IsLocked(permission) || Granted.Contains(permission);
}
