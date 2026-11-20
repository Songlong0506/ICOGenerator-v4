using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Configuration;

/// <summary>
/// Cấu hình đăng nhập (bind từ section "IdentityServer"). Mặc định app bắt buộc đăng nhập SSO qua
/// IdentityServer; riêng khi <see cref="LocalRole"/> có giá trị thì bỏ qua SSO và tự đăng nhập cục bộ
/// (xem <see cref="IsLocalLogin"/>). Phần bridge (xem <c>SsoUserProvisioner</c>) ánh xạ danh tính SSO về
/// một AppUser theo claim "username" (chưa có thì tự tạo) để gắn quyền sở hữu, hoặc từ chối truy cập khi
/// thiếu username. Vai trò của phiên KHÔNG lưu DB: lấy từ role claim qua <see cref="MapRoles"/> rồi phát
/// thành các claim Role.
/// </summary>
public class IdentityServerSettings
{
    public const string SectionName = "IdentityServer";

    /// <summary>
    /// Vai trò phát cho phiên đăng nhập cục bộ — đồng thời là CÔNG TẮC chọn kiểu đăng nhập: trống/thiếu
    /// ⇒ SSO bình thường; có giá trị ⇒ KHÔNG có SSO, app tự đăng nhập bằng user mang ĐÚNG TÊN vai trò này
    /// (vd "TeamDev", tự tạo ở lần đăng nhập đầu) và phát đúng vai trò này. Ở chế độ đó config là NGUỒN DUY NHẤT của vai trò (không có IdP, DB không lưu vai trò). Mặc định
    /// <c>null</c> có chủ đích: quên key thì rơi về SSO, không bao giờ rơi về "ai cũng là SuperAdmin". Kiểu
    /// enum nullable nên chuỗi gõ sai (vd "Admn") làm app dừng lúc khởi động thay vì lặng lẽ đổi chế độ.
    /// Chỉ dùng cho dev/nội bộ.
    /// </summary>
    public UserRole? LocalRole { get; set; }

    /// <summary>True khi <see cref="LocalRole"/> có giá trị ⇒ tự đăng nhập cục bộ, không dùng SSO.</summary>
    public bool IsLocalLogin => LocalRole.HasValue;

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
    /// Ánh xạ tập role claim của SSO về TẬP <see cref="UserRole"/> của app — giữ lại TẤT CẢ vai trò khớp,
    /// không rút gọn về vai trò "cao nhất": quyền giữa các vai trò giao nhau chứ không lồng nhau, gộp về
    /// một vai trò sẽ làm mất quyền mà chỉ vai trò kia mới có. Quyền hiệu lực = hợp quyền của cả tập
    /// (xem <c>PermissionService</c>). Nguồn ánh xạ là <see cref="SsoRoleClaimAttribute"/> khai báo trên
    /// từng <see cref="UserRole"/> (xem <see cref="SsoRoleClaims"/>), thay cho bảng cấu hình
    /// "IdentityServer:RoleMappings" trước đây. Trả về tập RỖNG khi không claim nào khớp — để bên gọi
    /// quyết định (giữ nguyên vai trò của user cũ, hoặc vai trò mặc định cho user mới). So khớp không
    /// phân biệt hoa/thường; claim trùng nhau chỉ tính một lần.
    /// </summary>
    public IReadOnlySet<UserRole> MapRoles(IEnumerable<string> ssoRoles)
    {
        var mapped = new HashSet<UserRole>();
        foreach (var raw in ssoRoles)
        {
            var ssoRole = raw?.Trim();
            if (string.IsNullOrEmpty(ssoRole))
                continue;

            if (SsoRoleClaims.Resolve(ssoRole) is UserRole mappedRole)
                mapped.Add(mappedRole);
        }
        return mapped;
    }
}
