using System.Reflection;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Gắn chuỗi role claim của IdentityServer (vd "HCP_CBO_API.CBO.ADMIN") vào từng <see cref="UserRole"/>.
/// Đây là NGUỒN SỰ THẬT DUY NHẤT cho ánh xạ claim → vai trò (trước đây nằm ở "IdentityServer:RoleMappings"
/// trong appsettings). Nhờ vậy thêm/sửa vai trò chỉ cần đụng enum, không phải đồng bộ thêm ở config.
/// Lưu ý: chuỗi claim gắn theo APIName của IdP (Bosch: HCP_CBO_API); nếu triển khai ở IdP có prefix khác
/// thì phải sửa ngay tại đây và build lại (đánh đổi khi bỏ cấu hình động qua appsettings).
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SsoRoleClaimAttribute : Attribute
{
    public SsoRoleClaimAttribute(string claim) => Claim = claim;

    /// <summary>Chuỗi role claim bên IdentityServer tương ứng với vai trò này.</summary>
    public string Claim { get; }
}

/// <summary>
/// Tra cứu <see cref="UserRole"/> từ chuỗi role claim của IdentityServer dựa trên
/// <see cref="SsoRoleClaimAttribute"/> khai báo trên enum. Bảng ánh xạ dựng MỘT LẦN (reflection) rồi cache;
/// so khớp KHÔNG phân biệt hoa/thường (claim của IdP có thể tới ở nhiều kiểu chữ).
/// </summary>
public static class SsoRoleClaims
{
    private static readonly IReadOnlyDictionary<string, UserRole> ByClaim = BuildMap();

    private static IReadOnlyDictionary<string, UserRole> BuildMap()
    {
        var map = new Dictionary<string, UserRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in Enum.GetValues<UserRole>())
        {
            var claim = typeof(UserRole).GetField(role.ToString())
                ?.GetCustomAttribute<SsoRoleClaimAttribute>()?.Claim;
            if (!string.IsNullOrWhiteSpace(claim))
                map[claim] = role;
        }
        return map;
    }

    /// <summary>Vai trò khớp chuỗi claim, hoặc null nếu không vai trò nào khai báo claim đó.</summary>
    public static UserRole? Resolve(string ssoRole) =>
        ByClaim.TryGetValue(ssoRole, out var role) ? role : null;
}
