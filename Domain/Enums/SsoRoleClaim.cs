using System.Reflection;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Gắn chuỗi role claim của IdentityServer (vd "HCP_CBO_API.CBO.ADMIN") vào từng <see cref="UserRole"/>
/// dựng sẵn. Phân quyền KHÔNG đi qua enum này — bảng RolePermission khóa theo đúng tên role IS4 (xem
/// <see cref="Security.RoleNames"/>), nên role mới tạo bên IS4 cấu hình quyền được ngay. Attribute chỉ còn
/// dùng ở ba chỗ cần một role cố định khi chưa có/không có IS4: chế độ đăng nhập cục bộ (phát claim của
/// vai trò <c>LocalRole</c>), ô khóa của Admin, và seed quyền mặc định.
/// Lưu ý: chuỗi claim gắn theo APIName của IdP (Bosch: HCP_CBO_API); nếu triển khai ở IdP có prefix khác
/// thì phải sửa ngay tại đây và build lại.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SsoRoleClaimAttribute : Attribute
{
    public SsoRoleClaimAttribute(string claim) => Claim = claim;

    /// <summary>Chuỗi role claim bên IdentityServer tương ứng với vai trò này.</summary>
    public string Claim { get; }
}

public static class SsoRoleClaims
{
    /// <summary>
    /// Tên role IS4 (đã chuẩn hoá bằng <see cref="Security.RoleNames.Normalize"/>) của một vai trò dựng sẵn.
    /// Enum thiếu attribute là lỗi lập trình ⇒ ném ngay thay vì trả chuỗi rỗng rồi cấp quyền nhầm role.
    /// </summary>
    public static string GetSsoRoleName(this UserRole role)
    {
        var claim = typeof(UserRole).GetField(role.ToString())
            ?.GetCustomAttribute<SsoRoleClaimAttribute>()?.Claim;
        if (string.IsNullOrWhiteSpace(claim))
            throw new InvalidOperationException($"UserRole.{role} is missing [SsoRoleClaim].");
        return Security.RoleNames.Normalize(claim);
    }
}
