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

    /// <summary>Claim trong token dùng làm định danh user (ánh xạ về AppUser.Username, quyền sở hữu, audit).
    /// Mặc định "preferred_username" (thường là NTID Bosch). Nếu trống hoặc không có sẽ lần lượt thử
    /// preferred_username → email → name → sub.</summary>
    public string UsernameClaim { get; set; } = "preferred_username";

    /// <summary>Tự tạo AppUser khi user đăng nhập SSO lần đầu (chưa có trong bảng). true (mặc định) ⇒
    /// rollout mượt, user mới nhận <see cref="DefaultRole"/> (chỉ xem), admin nâng quyền sau ở màn hình
    /// Roles. false ⇒ user chưa được admin tạo sẵn sẽ bị từ chối truy cập.</summary>
    public bool AutoProvisionUsers { get; set; } = true;

    /// <summary>Vai trò gán cho user tự tạo qua SSO (khi <see cref="AutoProvisionUsers"/> = true).</summary>
    public UserRole DefaultRole { get; set; } = UserRole.User;
}
