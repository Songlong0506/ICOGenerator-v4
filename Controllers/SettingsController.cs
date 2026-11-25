using ICOGenerator.Localization;
using ICOGenerator.Application.Settings;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

[RequirePermission(AppPermission.SettingsView)]
public class SettingsController : Controller
{
    private readonly GetAppSettingsQuery _getAppSettingsQuery;
    private readonly UpdateAppSettingsUseCase _updateAppSettingsUseCase;
    private readonly CheckEnvironmentQuery _checkEnvironmentQuery;

    public SettingsController(
        GetAppSettingsQuery getAppSettingsQuery,
        UpdateAppSettingsUseCase updateAppSettingsUseCase,
        CheckEnvironmentQuery checkEnvironmentQuery)
    {
        _getAppSettingsQuery = getAppSettingsQuery;
        _updateAppSettingsUseCase = updateAppSettingsUseCase;
        _checkEnvironmentQuery = checkEnvironmentQuery;
    }

    public async Task<IActionResult> Index()
    {
        return View(await _getAppSettingsQuery.ExecuteAsync());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.SettingsManage)]
    public async Task<IActionResult> Update(AppSettingsVm input)
    {
        var error = await _updateAppSettingsUseCase.ExecuteAsync(input);

        if (error is null)
            TempData["Success"] = Loc.T("SettingsController.Update");
        else
            TempData["Error"] = error;

        return RedirectToAction(nameof(Index));
    }

    // POST + SettingsManage (không phải SettingsView): lượt kiểm gọi thử từng model LLM (tốn vài token), ghi file
    // tạm, và lần đầu có thể tải Chromium — là thao tác của người vận hành, không phải của người chỉ xem.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.SettingsManage)]
    public async Task<IActionResult> TestEnvironment(CancellationToken cancellationToken)
    {
        return Json(await _checkEnvironmentQuery.ExecuteAsync(Request.IsHttps, cancellationToken));
    }
}
