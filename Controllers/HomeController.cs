using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Hứng lỗi non-Development cho app.UseExceptionHandler("/Home/Error"): trước đây route này KHÔNG tồn tại
// (không có HomeController) nên mọi exception ngoài Development trả 404 thay vì trang lỗi.
public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    // Living styleguide for the standardized Bosch form-kit components
    // (fields, checkbox, switch, dropdown, data-table indicators). Kept
    // anonymous so it doubles as a design reference and a visual-test target.
    [AllowAnonymous]
    public IActionResult UiKit() => View();

    // Reachable without login so an error around authentication shows the error page instead of bouncing to /Account/Login.
    [AllowAnonymous]
    public IActionResult Error()
    {
        var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        if (feature?.Error is { } ex)
            _logger.LogError(ex, "Lỗi chưa xử lý khi xử lý {Path}.", feature.Path);

        return View();
    }
}
