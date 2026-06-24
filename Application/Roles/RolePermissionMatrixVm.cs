using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;

namespace ICOGenerator.Application.Roles;

/// <summary>
/// Dữ liệu cho màn hình cấu hình Roles &amp; Permissions: các màn hình (hàng) × các role (cột).
/// Admin là cột "khóa" (luôn đủ quyền) nên không cho chỉnh để tránh tự khóa mình.
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

    /// <summary>Admin: hiển thị tích sẵn và không cho sửa (implicit-all trong PermissionService).</summary>
    public bool Locked { get; init; }

    public HashSet<AppPermission> Granted { get; init; } = new();

    public bool Has(AppPermission permission) => Locked || Granted.Contains(permission);
}
