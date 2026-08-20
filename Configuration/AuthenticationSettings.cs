using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Configuration;

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

    /// <summary>
    /// Tài khoản <c>AppUser</c> mà chế độ <see cref="AuthProvider.Local"/> tự đăng nhập vào (khớp theo
    /// <c>Username</c>, không phân biệt hoa/thường). Phải là một tài khoản có thật — <c>DbInitializer</c>
    /// seed sẵn superadmin/admin/teamdev/user. Đổi tên này để chạy máy dev dưới danh tính khác mà không
    /// phải tạo tài khoản mới.
    /// </summary>
    public string LocalUsername { get; set; } = "superadmin";

    /// <summary>
    /// Vai trò phát cho phiên đăng nhập <see cref="AuthProvider.Local"/>. Đây là NGUỒN DUY NHẤT của vai
    /// trò ở chế độ Local: không có IdP để lấy role claim, và DB không lưu vai trò của ai cả. Mặc định
    /// <see cref="UserRole.SuperAdmin"/> (toàn quyền, không tự khóa được) nên máy dev luôn đủ quyền; đặt
    /// vai trò thấp hơn khi cần thử màn hình dưới góc nhìn quyền hạn chế.
    /// </summary>
    public UserRole LocalRole { get; set; } = UserRole.SuperAdmin;
}
