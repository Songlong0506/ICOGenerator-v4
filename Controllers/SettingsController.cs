using ICOGenerator.Application.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

public class SettingsController : Controller
{
    private readonly GetAppSettingsQuery _getAppSettingsQuery;
    private readonly UpdateAppSettingsUseCase _updateAppSettingsUseCase;

    public SettingsController(
        GetAppSettingsQuery getAppSettingsQuery,
        UpdateAppSettingsUseCase updateAppSettingsUseCase)
    {
        _getAppSettingsQuery = getAppSettingsQuery;
        _updateAppSettingsUseCase = updateAppSettingsUseCase;
    }

    public async Task<IActionResult> Index()
    {
        return View(await _getAppSettingsQuery.ExecuteAsync());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(AppSettingsVm input)
    {
        var error = await _updateAppSettingsUseCase.ExecuteAsync(input);

        if (error is null)
            TempData["Success"] = "Settings saved. Hầu hết thay đổi có hiệu lực ngay; riêng connection string cần khởi động lại ứng dụng.";
        else
            TempData["Error"] = error;

        return RedirectToAction(nameof(Index));
    }
}
