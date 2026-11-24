using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;
using ICOGenerator.Services.Security;

namespace ICOGenerator.Application.Roles;

/// <summary>
/// Dữ liệu cho popup Roles &amp; Permissions: các màn hình (hàng) × các role IS4 (cột).
/// Ô Admin × <see cref="PermissionService.AdminLockedPermission"/> bị khóa (luôn có) để Admin không tự khóa mình.
/// </summary>
public class RolePermissionMatrixVm
{
    public IReadOnlyList<PermissionScreen> Screens { get; init; } = PermissionCatalog.Screens;
    public IReadOnlyList<RolePermissionColumn> Columns { get; init; } = Array.Empty<RolePermissionColumn>();

    /// <summary>Không lấy được danh sách role từ IdentityServer — cột chỉ gồm role đã có quyền trong DB.</summary>
    public bool IdentityServerUnavailable { get; init; }
}

public class RolePermissionColumn
{
    /// <summary>Tên role IS4 đã chuẩn hoá (xem <see cref="RoleNames"/>) — cũng là giá trị lưu DB.</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>Nhãn ngắn trên tiêu đề cột (<see cref="RoleNames.ShortLabel"/>); tên đầy đủ ở tooltip.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>True = lấy được danh sách role IS4 mà role này không có trong đó (đã xoá/đổi tên bên IS4).
    /// IS4 không với tới thì luôn false — không biết thì không kết luận.</summary>
    public bool MissingFromIdentityServer { get; init; }

    public HashSet<AppPermission> Granted { get; init; } = new();

    /// <summary>Ô luôn được cấp, không cho sửa: hiển thị dấu khóa thay cho checkbox.</summary>
    public bool IsLocked(AppPermission permission) =>
        Role == RoleNames.Admin && permission == PermissionService.AdminLockedPermission;

    public bool Has(AppPermission permission) => IsLocked(permission) || Granted.Contains(permission);
}
