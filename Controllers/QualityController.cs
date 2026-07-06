using ICOGenerator.Application.Quality;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Báo cáo chất lượng giao hàng (thông lượng + rước việc + độ tin cậy model). Bổ trợ trang Usage (chi phí).
[RequirePermission(AppPermission.QualityView)]
public class QualityController : Controller
{
    private readonly GetDeliveryQualityQuery _getDeliveryQuality;

    public QualityController(GetDeliveryQualityQuery getDeliveryQuality)
    {
        _getDeliveryQuality = getDeliveryQuality;
    }

    public async Task<IActionResult> Index(int? year)
    {
        return View(await _getDeliveryQuality.ExecuteAsync(year, HttpContext.RequestAborted));
    }
}
