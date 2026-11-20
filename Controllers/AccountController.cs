using ICOGenerator.Localization;
using System.Security.Claims;
using ICOGenerator.Configuration;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Đăng nhập của app rẽ theo IdentityServer:LocalRole (appsettings): trống ⇒ bắt buộc đăng nhập SSO qua
// OpenID Connect; có giá trị ⇒ KHÔNG có form username/password — tự đăng nhập bằng user mang
// đúng tên vai trò đó và phát đúng vai trò đó. Đăng nhập/đăng xuất bằng mật khẩu tự viết đã bị bỏ (cùng với cột
// AppUser.PasswordHash).
public class AccountController : Controller
{
    private readonly IdentityServerSettings _authSettings;
    private readonly SsoUserProvisioner _users;

    public AccountController(IdentityServerSettings authSettings, SsoUserProvisioner users)
    {
        _authSettings = authSettings;
        _users = users;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToLocal(returnUrl);

        // SSO: đẩy thẳng người dùng sang IdentityServer. Cookie LoginPath cũng trỏ vào đây nên mọi trang
        // cần đăng nhập sẽ tự động chuyển hướng SSO.
        if (!_authSettings.IsLocalLogin)
        {
            var target = Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action("Index", "Projects")!;
            return Challenge(
                new AuthenticationProperties { RedirectUri = target },
                OpenIdConnectDefaults.AuthenticationScheme);
        }

        // Cục bộ: không có đăng nhập bằng mật khẩu — tự đăng nhập bằng user mang tên LocalRole.
        return await SignInLocalAsync(_authSettings.LocalRole!.Value, returnUrl);
    }

    // Đăng nhập cục bộ (IdentityServer:LocalRole có giá trị): phát cookie cho user mang ĐÚNG TÊN vai trò đó
    // (vd LocalRole = TeamDev ⇒ user "TeamDev"). Không seed sẵn tài khoản nào: user được tra (không phân biệt
    // hoa/thường) hoặc tự tạo ở lần đăng nhập đầu, qua cùng cầu nối với SSO. Vai trò KHÔNG tra từ DB — DB
    // không lưu vai trò của ai cả — mà lấy thẳng từ config. Đây là chỗ thay thế cho role claim của IdP ở chế
    // độ không có IdP. Gọi khi cookie LoginPath redirect người dùng chưa đăng nhập tới.
    private async Task<IActionResult> SignInLocalAsync(UserRole role, string? returnUrl)
    {
        var admin = await _users.ResolveOrProvisionAsync(
            role.ToString(), role.GetTitle(), email: null, cancellationToken: HttpContext.RequestAborted);

        // Chỉ null khi username rỗng — tên enum không bao giờ rỗng, nhưng cookie trống sẽ biến thành
        // AccessDenied ở mọi trang mà không nói vì sao, nên vẫn báo lỗi rõ.
        if (admin is null)
            return StatusCode(StatusCodes.Status500InternalServerError,
                Loc.T("AccountController.SignInLocalAsync", role));

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, admin.Username),
            // Tên hiển thị cho UI (left menu…); tách khỏi claim Name vì Name là NTID lái quyền sở hữu.
            new("display_name", string.IsNullOrWhiteSpace(admin.DisplayName) ? admin.Username : admin.DisplayName),
            // Vai trò của phiên: PermissionService đọc đúng các claim này (xem AppUser — không có bản sao DB).
            new(ClaimTypes.Role, role.ToString())
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

        // Chế độ cục bộ không có IdP để đăng nhập lại ⇒ chỉ quay về trang đích.
        if (_authSettings.IsLocalLogin)
            return LocalRedirect(target);

        return Challenge(
            new AuthenticationProperties { RedirectUri = target },
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    // Đăng xuất chỉ có ý nghĩa ở chế độ SSO (RP-initiated logout khỏi IdentityServer để không bị đăng nhập
    // lại ngầm qua phiên còn sống ở IdP). Ở chế độ cục bộ, request kế sẽ tự đăng nhập lại ngay nên nút
    // Logout được ẩn (xem _Layout) — vẫn giữ endpoint để phòng gọi trực tiếp.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (!_authSettings.IsLocalLogin)
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
