using ICOGenerator.Application.Quality;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Báo cáo chất lượng giao hàng (thông lượng + rước việc + độ tin cậy model) và ma trận truy vết yêu cầu.
// Mặc định cả controller chỉ cần quyền xem; action phân tích (đốt token thật) yêu cầu QualityManage.
[RequirePermission(AppPermission.QualityView)]
public class QualityController : Controller
{
    private readonly GetDeliveryQualityQuery _getDeliveryQuality;
    private readonly GetTraceabilityPageQuery _getTraceabilityPage;
    private readonly BuildTraceabilityMatrixUseCase _buildTraceability;

    public QualityController(
        GetDeliveryQualityQuery getDeliveryQuality,
        GetTraceabilityPageQuery getTraceabilityPage,
        BuildTraceabilityMatrixUseCase buildTraceability)
    {
        _getDeliveryQuality = getDeliveryQuality;
        _getTraceabilityPage = getTraceabilityPage;
        _buildTraceability = buildTraceability;
    }

    public async Task<IActionResult> Index(int? year)
    {
        return View(await _getDeliveryQuality.ExecuteAsync(year, HttpContext.RequestAborted));
    }

    public async Task<IActionResult> Traceability(Guid? projectId)
    {
        return View(await _getTraceabilityPage.ExecuteAsync(projectId, HttpContext.RequestAborted));
    }

    // Đồng bộ theo yêu cầu (fetch chờ + spinner như chat BA): một lời gọi LLM, xong thì JS reload trang.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.QualityManage)]
    public async Task<IActionResult> BuildTraceability(Guid projectId)
    {
        var result = await _buildTraceability.ExecuteAsync(projectId, User.Identity?.Name, HttpContext.RequestAborted);
        return Json(new { ok = result.Ok, error = result.Error });
    }
}
