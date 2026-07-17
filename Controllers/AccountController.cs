using System.Security.Claims;
using ICOGenerator.Application.Account;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

public class AccountController : Controller
{
    private readonly LoginUseCase _loginUseCase;
    private readonly AuthenticationSettings _authSettings;

    public AccountController(LoginUseCase loginUseCase, AuthenticationSettings authSettings)
    {
        _loginUseCase = loginUseCase;
        _authSettings = authSettings;
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToLocal(returnUrl);

        // SSO: bỏ qua form tự code, đẩy thẳng người dùng sang IdentityServer. Cookie LoginPath cũng trỏ
        // vào đây nên mọi trang cần đăng nhập sẽ tự động chuyển hướng SSO.
        if (_authSettings.Provider == AuthProvider.IdentityServer)
        {
            var target = Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action("Index", "Projects")!;
            return Challenge(
                new AuthenticationProperties { RedirectUri = target },
                OpenIdConnectDefaults.AuthenticationScheme);
        }

        ViewBag.ReturnUrl = returnUrl;
        return View(new LoginVm());
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginVm vm, string? returnUrl = null)
    {
        // Ở chế độ SSO không có đăng nhập bằng mật khẩu cục bộ: quay về GET để challenge IdentityServer.
        if (_authSettings.Provider == AuthProvider.IdentityServer)
            return RedirectToAction(nameof(Login), new { returnUrl });

        ViewBag.ReturnUrl = returnUrl;

        if (!ModelState.IsValid)
            return View(vm);

        var user = await _loginUseCase.ExecuteAsync(vm.Username, vm.Password);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "Sai tên đăng nhập hoặc mật khẩu.");
            return View(vm);
        }

        // Claim Role lái toàn bộ phân quyền: authorization filter và menu sidebar đọc role này rồi
        // tra quyền qua PermissionService.
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString())
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return RedirectToLocal(returnUrl);
    }

    // Intentionally NOT [AllowAnonymous]: the fallback policy already requires auth, and
    // [ValidateAntiForgeryToken] blocks CSRF forced-logout.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // SSO: đăng xuất luôn khỏi IdentityServer (RP-initiated logout) để không bị đăng nhập lại ngầm
        // qua phiên còn sống ở IdP. SaveTokens = true nên handler tự đính id_token_hint.
        if (_authSettings.Provider == AuthProvider.IdentityServer)
        {
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
