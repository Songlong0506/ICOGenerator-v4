using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Application.Account;

/// <summary>
/// Cấu hình đăng nhập SSO qua IdentityServer (bind từ section "IdentityServer"). Chỉ có tác dụng khi
/// Authentication:Provider = "IdentityServer". Vì app phân quyền theo bảng AppUser (claim Role) và gắn
/// quyền sở hữu theo username, phần bridge (xem <c>SsoUserProvisioner</c>) sẽ ánh xạ danh tính SSO về
/// một AppUser: tra theo <see cref="UsernameClaim"/>, tự tạo user mới với <see cref="DefaultRole"/> khi
/// bật <see cref="AutoProvisionUsers"/>, hoặc từ chối truy cập nếu không.
/// </summary>
public class IdentityServerSettings
{
    public const string SectionName = "IdentityServer";

    /// <summary>URL gốc IdentityServer (Authority) — dùng để lấy metadata OIDC.</summary>
    public string BaseURL { get; set; } = string.Empty;

    /// <summary>Tên API scope xin thêm khi đăng nhập (ngoài openid/profile/email). Trống ⇒ bỏ qua.</summary>
    public string APIName { get; set; } = string.Empty;

    /// <summary>Client Id đăng ký với IdentityServer.</summary>
    public string Client_Id { get; set; } = string.Empty;

    /// <summary>Client secret cho confidential client. Trống ⇒ client public (implicit/PKCE) như mẫu Bosch.
    /// KHÔNG commit secret thật; nạp qua biến môi trường IdentityServer__ClientSecret hoặc user-secrets.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>response_type OIDC. Mặc định "token id_token" (implicit) theo mẫu Bosch.</summary>
    public string ResponseType { get; set; } = "token id_token";

    /// <summary>Bắt buộc HTTPS khi lấy metadata. Mẫu Bosch để false (IdP nội bộ chứng chỉ tự ký).</summary>
    public bool RequireHttpsMetadata { get; set; }

    /// <summary>Ánh xạ giá trị claim role của IdentityServer → <see cref="UserRole"/> của app
    /// (không phân biệt hoa/thường). Vd { "HCP_CBO_API.CBO.ADMIN": "Admin" }. User nhận vai trò CAO NHẤT
    /// khớp được; không khớp claim nào ⇒ <see cref="MapRole"/> trả null (giữ vai trò cũ / dùng DefaultRole).</summary>
    public Dictionary<string, UserRole> RoleMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ánh xạ tập role claim của SSO về <see cref="UserRole"/> của app, chọn vai trò CAO NHẤT
    /// (Admin &gt; TeamDev &gt; User). Trả về null khi KHÔNG có claim nào khớp <see cref="RoleMappings"/> —
    /// để bên gọi quyết định (giữ vai trò user cũ hoặc dùng <see cref="DefaultRole"/> cho user mới).
    /// So khớp không phân biệt hoa/thường nên vẫn đúng dù config binding không giữ comparer của Dictionary.
    /// </summary>
    public UserRole? MapRole(IEnumerable<string> ssoRoles)
    {
        UserRole? best = null;
        foreach (var raw in ssoRoles)
        {
            var ssoRole = raw?.Trim();
            if (string.IsNullOrEmpty(ssoRole))
                continue;

            foreach (var (mappedKey, mappedRole) in RoleMappings)
            {
                if (!string.Equals(mappedKey, ssoRole, StringComparison.OrdinalIgnoreCase))
                    continue;
                // Enum: Admin=0 < TeamDev=1 < User=2 ⇒ giá trị NHỎ hơn = quyền cao hơn; giữ vai trò cao nhất.
                if (best is null || mappedRole < best)
                    best = mappedRole;
            }
        }
        return best;
    }
}
