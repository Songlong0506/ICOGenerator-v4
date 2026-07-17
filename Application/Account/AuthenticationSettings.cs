namespace ICOGenerator.Application.Account;

/// <summary>
/// Cách người dùng đăng nhập vào app, chọn qua config "Authentication:Provider".
/// </summary>
public enum AuthProvider
{
    /// <summary>Đăng nhập bằng tài khoản tự quản trong bảng AppUser (form username/password của app).</summary>
    Local = 0,

    /// <summary>Đăng nhập SSO qua IdentityServer của Bosch (OpenID Connect).</summary>
    IdentityServer = 1
}

/// <summary>
/// Cờ chọn kiểu đăng nhập (bind từ section "Authentication"). Mặc định <see cref="AuthProvider.Local"/>
/// để giữ nguyên hành vi cũ; đặt "IdentityServer" khi triển khai thật để bắt buộc đăng nhập SSO. Trong
/// tương lai khi bỏ hẳn login tự code, chỉ cần đổi cờ này (và section "IdentityServer") — không phải build lại.
/// </summary>
public class AuthenticationSettings
{
    public const string SectionName = "Authentication";

    public AuthProvider Provider { get; set; } = AuthProvider.Local;
}
