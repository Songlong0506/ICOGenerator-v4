using System.Security.Claims;
using ICOGenerator.Application.Account;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

public class AccountController : Controller
{
    private readonly LoginUseCase _loginUseCase;

    public AccountController(LoginUseCase loginUseCase) => _loginUseCase = loginUseCase;

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToLocal(returnUrl);

        ViewBag.ReturnUrl = returnUrl;
        return View(new LoginVm());
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginVm vm, string? returnUrl = null)
    {
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
        return RedirectToAction(nameof(Login));
    }

    // Trang báo "không đủ quyền" — đích của cookie AccessDeniedPath khi user đã đăng nhập nhưng
    // RequirePermission từ chối. Vẫn yêu cầu đăng nhập (không [AllowAnonymous]).
    [HttpGet]
    public IActionResult AccessDenied() => View();

    // Only redirect to a local path so a crafted ?returnUrl can't become an open redirect.
    private IActionResult RedirectToLocal(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl!) : RedirectToAction("Index", "Projects");
}
