using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain.Security;

/// <summary>
/// Quy ước tên role dùng cho phân quyền: chính là tên role của API resource trên IdentityServer (vd
/// "HCP_CBO_API.CBO.ADMIN"), không qua bảng ánh xạ nào. Claim Role của phiên, cột <c>RolePermission.Role</c>
/// và cột của ma trận Roles &amp; Permissions đều dùng chung dạng đã chuẩn hoá ở đây (bỏ khoảng trắng, viết
/// HOA) — IdP có thể phát claim ở nhiều kiểu chữ, còn so khớp claim (<c>IsInRole</c>) và SQL Server/Sqlite
/// lại khác nhau về phân biệt hoa/thường, nên chuẩn hoá MỘT lần ở cửa vào thay vì so khớp lỏng ở mọi nơi.
/// </summary>
public static class RoleNames
{
    /// <summary>Độ dài tối đa của tên role lưu DB.</summary>
    public const int MaxLength = 200;

    /// <summary>Role luôn giữ quyền quản lý ma trận (ô khóa) — xem <c>PermissionService.AdminLockedPermission</c>.</summary>
    public static string Admin { get; } = UserRole.Admin.GetSsoRoleName();

    /// <summary>Dạng chuẩn của một tên role; chuỗi rỗng khi đầu vào rỗng/null.</summary>
    public static string Normalize(string? role) => role?.Trim().ToUpperInvariant() ?? string.Empty;

    /// <summary>
    /// Nhãn ngắn cho UI: đoạn sau dấu chấm cuối ("HCP_CBO_API.CBO.ADMIN" ⇒ "ADMIN"). Tên đầy đủ vẫn hiện ở
    /// tooltip — tiền tố API giống nhau ở mọi role nên chỉ làm cột ma trận rộng ra.
    /// </summary>
    public static string ShortLabel(string role)
    {
        var dot = role.LastIndexOf('.');
        return dot >= 0 && dot < role.Length - 1 ? role[(dot + 1)..] : role;
    }
}
