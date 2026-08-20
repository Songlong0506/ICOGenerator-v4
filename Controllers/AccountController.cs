using System.Security.Claims;
using ICOGenerator.Configuration;
using ICOGenerator.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Controllers;

// Đăng nhập của app rẽ theo cờ Authentication:Provider (appsettings): 'IdentityServer' bắt buộc đăng nhập
// SSO qua OpenID Connect; 'Local' KHÔNG có form username/password — tự đăng nhập bằng tài khoản seed sẵn
// (Authentication:LocalUsername) với vai trò lấy từ Authentication:LocalRole. Đăng nhập/đăng xuất bằng mật
// khẩu tự viết đã bị bỏ (cùng với cột AppUser.PasswordHash).
public class AccountController : Controller
{
    private readonly AuthenticationSettings _authSettings;
    private readonly AppDbContext _db;

    public AccountController(AuthenticationSettings authSettings, AppDbContext db)
    {
        _authSettings = authSettings;
        _db = db;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToLocal(returnUrl);

        // SSO: đẩy thẳng người dùng sang IdentityServer. Cookie LoginPath cũng trỏ vào đây nên mọi trang
        // cần đăng nhập sẽ tự động chuyển hướng SSO.
        if (_authSettings.Provider == AuthProvider.IdentityServer)
        {
            var target = Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action("Index", "Projects")!;
            return Challenge(
                new AuthenticationProperties { RedirectUri = target },
                OpenIdConnectDefaults.AuthenticationScheme);
        }

        // Local: không có đăng nhập bằng mật khẩu — tự đăng nhập bằng tài khoản Admin seed sẵn.
        return await SignInLocalAdminAsync(returnUrl);
    }

    // Đăng nhập cục bộ mặc định: phát cookie theo tài khoản cấu hình ở Authentication:LocalUsername
    // (mặc định 'superadmin', DbInitializer.SeedUsersAsync tạo sẵn khi DB còn trống). Vai trò KHÔNG tra từ
    // DB — DB không lưu vai trò của ai cả — mà lấy thẳng từ Authentication:LocalRole (mặc định SuperAdmin,
    // toàn quyền và không tự khóa được, nên dev cục bộ luôn đủ quyền). Đây là chỗ thay thế cho role claim của
    // IdP ở chế độ không có IdP. Gọi khi cookie LoginPath redirect người dùng chưa đăng nhập tới.
    private async Task<IActionResult> SignInLocalAdminAsync(string? returnUrl)
    {
        // Cấu hình để trống ⇒ dừng ngay: đi tiếp sẽ khớp phải một user tên rỗng hoặc phát cookie vô danh.
        var configured = _authSettings.LocalUsername?.Trim();
        if (string.IsNullOrEmpty(configured))
            return StatusCode(StatusCodes.Status500InternalServerError,
                "Chưa cấu hình Authentication:LocalUsername để đăng nhập cục bộ.");

        // So khớp không phân biệt hoa/thường như luồng SSO (Sqlite mặc định phân biệt, SQL Server thì không).
        var lowered = configured.ToLower();
        var admin = await _db.AppUsers
            .AsNoTracking()
            .Where(u => u.Username.ToLower() == lowered)
            .OrderBy(u => u.CreatedAt) // deterministic nếu index unique bị mất ở install cũ
            .Select(u => new { u.Username, u.DisplayName })
            .FirstOrDefaultAsync(HttpContext.RequestAborted);

        // Không có tài khoản nào mang tên đó (DB rỗng bất thường / cấu hình sai) ⇒ báo lỗi rõ thay vì phát
        // cookie trống, vì cookie trống sẽ biến thành AccessDenied ở mọi trang mà không nói vì sao.
        if (admin is null)
            return StatusCode(StatusCodes.Status500InternalServerError,
                $"Không tìm thấy tài khoản '{configured}' để đăng nhập cục bộ (Authentication:LocalUsername).");

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, admin.Username),
            // Tên hiển thị cho UI (left menu…); tách khỏi claim Name vì Name là NTID lái quyền sở hữu.
            new("display_name", string.IsNullOrWhiteSpace(admin.DisplayName) ? admin.Username : admin.DisplayName),
            // Vai trò của phiên: PermissionService đọc đúng các claim này (xem AppUser — không có bản sao DB).
            new(ClaimTypes.Role, _authSettings.LocalRole.ToString())
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return RedirectToLocal(returnUrl);
    }

    // Buộc challenge lại OIDC để làm mới access_token đã hết hạn (SaveTokens lưu access_token + expires_at
    // mới vào cookie), KỂ CẢ khi cookie app còn sống — khác Login vốn bounce về ngay nếu đã đăng nhập. Phiên
    // ở IdP thường còn sống nên vòng này im lặng (không phải gõ lại mật khẩu). Dùng bởi trang User Roles khi
    // phát hiện access_token hết hạn. [AllowAnonymous] để tự làm chủ việc challenge dù cookie đã hết hay chưa.
    [HttpGet]
    [AllowAnonymous]
    public IActionResult ReAuth(string? returnUrl = null)
    {
        var target = Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action("Index", "Projects")!;

        // Chế độ Local không có IdP để đăng nhập lại ⇒ chỉ quay về trang đích.
        if (_authSettings.Provider != AuthProvider.IdentityServer)
            return LocalRedirect(target);

        return Challenge(
            new AuthenticationProperties { RedirectUri = target },
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    // Đăng xuất chỉ có ý nghĩa ở chế độ SSO (RP-initiated logout khỏi IdentityServer để không bị đăng nhập
    // lại ngầm qua phiên còn sống ở IdP). Ở chế độ Local, request kế sẽ tự đăng nhập lại bằng Admin nên nút
    // Logout được ẩn (xem _Layout) — vẫn giữ endpoint để phòng gọi trực tiếp.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (_authSettings.Provider == AuthProvider.IdentityServer)
        {
            // SaveTokens = true nên handler tự đính id_token_hint.
            return SignOut(
                new AuthenticationProperties { RedirectUri = Url.Action(nameof(Login), "Account") },
                CookieAuthenticationDefaults.AuthenticationScheme,
                OpenIdConnectDefaults.AuthenticationScheme);
        }

        return RedirectToAction(nameof(Login));
    }

    // Trang báo "không đủ quyền" — đích của cookie AccessDeniedPath khi user đã đăng nhập nhưng
    // RequirePermission từ chối, VÀ đích khi đăng nhập SSO bị từ chối (user bị khóa / không được cấp).
    // [AllowAnonymous] để trường hợp SSO-từ-chối (chưa có cookie) không bị đẩy ngược về Login → tránh
    // vòng lặp challenge lại IdentityServer.
    [HttpGet]
    [AllowAnonymous]
    public IActionResult AccessDenied() => View();

    // Only redirect to a local path so a crafted ?returnUrl can't become an open redirect.
    private IActionResult RedirectToLocal(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl!) : RedirectToAction("Index", "Projects");
}
