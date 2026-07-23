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

    /// <summary>
    /// Ánh xạ tập role claim của SSO về <see cref="UserRole"/> của app, chọn vai trò CAO NHẤT
    /// (SuperAdmin &gt; Admin &gt; TeamDev &gt; User). Nguồn ánh xạ là <see cref="SsoRoleClaimAttribute"/> khai
    /// báo trên từng <see cref="UserRole"/> (xem <see cref="SsoRoleClaims"/>), thay cho bảng cấu hình
    /// "IdentityServer:RoleMappings" trước đây. Trả về null khi KHÔNG có claim nào khớp — để bên gọi quyết
    /// định (giữ vai trò user cũ hoặc dùng <see cref="DefaultRole"/> cho user mới). So khớp không phân biệt
    /// hoa/thường.
    /// </summary>
    public UserRole? MapRole(IEnumerable<string> ssoRoles)
    {
        UserRole? best = null;
        foreach (var raw in ssoRoles)
        {
            var ssoRole = raw?.Trim();
            if (string.IsNullOrEmpty(ssoRole))
                continue;

            if (SsoRoleClaims.Resolve(ssoRole) is not UserRole mappedRole)
                continue;

            // Giá trị enum KHÔNG phản ánh đặc quyền (SuperAdmin thêm sau với giá trị 3) nên so sánh
            // bằng thứ hạng tường minh để giữ vai trò cao nhất.
            if (best is null || PrivilegeRank(mappedRole) > PrivilegeRank(best.Value))
                best = mappedRole;
        }
        return best;
    }

    /// <summary>Thứ hạng đặc quyền (cao → thấp): SuperAdmin &gt; Admin &gt; TeamDev &gt; User.</summary>
    private static int PrivilegeRank(UserRole role) => role switch
    {
        UserRole.SuperAdmin => 3,
        UserRole.Admin => 2,
        UserRole.TeamDev => 1,
        UserRole.User => 0,
        _ => -1
    };
}
