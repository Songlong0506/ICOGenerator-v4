using ICOGenerator.Domain.Enums;
using ICOGenerator.Domain.Security;

namespace ICOGenerator.Configuration;

/// <summary>
/// Cấu hình đăng nhập (bind từ section "IdentityServer"). Mặc định app bắt buộc đăng nhập SSO qua
/// IdentityServer; riêng khi <see cref="LocalRole"/> có giá trị thì bỏ qua SSO và tự đăng nhập cục bộ
/// (xem <see cref="IsLocalLogin"/>). Phần bridge (xem <c>SsoUserProvisioner</c>) ánh xạ danh tính SSO về
/// một AppUser theo claim "username" (chưa có thì tự tạo) để gắn quyền sở hữu, hoặc từ chối truy cập khi
/// thiếu username. Vai trò của phiên KHÔNG lưu DB: lấy từ role claim qua <see cref="MapRoles"/> rồi phát
/// thành các claim Role (tên role IS4).
/// </summary>
public class IdentityServerSettings
{
    public const string SectionName = "IdentityServer";

    /// <summary>
    /// Vai trò phát cho phiên đăng nhập cục bộ — đồng thời là CÔNG TẮC chọn kiểu đăng nhập: trống/thiếu
    /// ⇒ SSO bình thường; có giá trị ⇒ KHÔNG có SSO, app tự đăng nhập bằng user mang ĐÚNG TÊN vai trò này
    /// (vd "TeamDev", tự tạo ở lần đăng nhập đầu) và phát claim Role là role IS4 tương ứng của vai trò này
    /// (<see cref="SsoRoleClaimAttribute"/>). Ở chế độ đó config là NGUỒN DUY NHẤT của vai trò (không có IdP, DB không lưu vai trò). Mặc định
    /// <c>null</c> có chủ đích: quên key thì rơi về SSO, không bao giờ rơi về "ai cũng là Admin". Kiểu
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
    /// Tập role claim của SSO ⇒ tập role của phiên: chính các tên role IS4, chỉ chuẩn hoá (xem
    /// <see cref="RoleNames.Normalize"/>), bỏ rỗng và trùng — KHÔNG ánh xạ qua enum nào. Giữ lại TẤT CẢ vì
    /// quyền giữa các role giao nhau chứ không lồng nhau; quyền hiệu lực = hợp quyền của cả tập (xem
    /// <c>PermissionService</c>). Role chưa được cấu hình trong ma trận thì đơn giản không có quyền gì.
    /// </summary>
    public IReadOnlySet<string> MapRoles(IEnumerable<string> ssoRoles)
    {
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in ssoRoles)
        {
            var role = RoleNames.Normalize(raw);
            if (role.Length > 0)
                mapped.Add(role);
        }
        return mapped;
    }
}
