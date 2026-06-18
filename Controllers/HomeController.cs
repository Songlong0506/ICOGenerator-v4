using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Hứng lỗi chưa được xử lý trong môi trường non-Development. Program.cs cấu hình
// app.UseExceptionHandler("/Home/Error"), nhưng trước đây route này KHÔNG tồn tại
// (không có HomeController) nên mọi exception ngoài Development trả về 404 thay vì
// một trang lỗi tử tế — đó cũng là lý do một số chỗ (vd BARequirementService) phải
// tự bắt lỗi inline. Action dưới đây render trang lỗi và ghi log chi tiết.
public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    // Reachable without a login so an error that happens before/around authentication still
    // shows the error page instead of bouncing to /Account/Login.
    [AllowAnonymous]
    public IActionResult Error()
    {
        var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        if (feature?.Error is { } ex)
            _logger.LogError(ex, "Lỗi chưa xử lý khi xử lý {Path}.", feature.Path);

        return View();
    }
}
