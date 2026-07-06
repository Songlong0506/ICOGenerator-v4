using ICOGenerator.Application.Prompts;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Prompt Studio: xem/sửa nội dung template prompt theo PHIÊN BẢN (bảng PromptTemplateVersions) —
// sửa prompt không cần deploy, rollback một cú nhấp, diff giữa các phiên bản. Mặc định cả controller
// chỉ cần quyền xem; các action ghi (đổi hành vi AI ngay lập tức) yêu cầu PromptManage.
[RequirePermission(AppPermission.PromptView)]
public class PromptsController : Controller
{
    private readonly GetPromptStudioPageQuery _getStudioPage;
    private readonly GetPromptDetailQuery _getDetail;
    private readonly GetPromptVersionDiffQuery _getDiff;
    private readonly SavePromptVersionUseCase _saveVersion;
    private readonly ActivatePromptVersionUseCase _activateVersion;
    private readonly RevertPromptToFileUseCase _revertToFile;

    public PromptsController(
        GetPromptStudioPageQuery getStudioPage,
        GetPromptDetailQuery getDetail,
        GetPromptVersionDiffQuery getDiff,
        SavePromptVersionUseCase saveVersion,
        ActivatePromptVersionUseCase activateVersion,
        RevertPromptToFileUseCase revertToFile)
    {
        _getStudioPage = getStudioPage;
        _getDetail = getDetail;
        _getDiff = getDiff;
        _saveVersion = saveVersion;
        _activateVersion = activateVersion;
        _revertToFile = revertToFile;
    }

    public async Task<IActionResult> Index()
    {
        return View(await _getStudioPage.ExecuteAsync(HttpContext.RequestAborted));
    }

    [HttpGet]
    public async Task<IActionResult> Detail(string key)
    {
        var vm = await _getDetail.ExecuteAsync(key, HttpContext.RequestAborted);
        if (vm == null)
            return NotFound("Prompt template not found.");

        return View(vm);
    }

    // from/to: số phiên bản DB, 0 = nội dung file trong repo.
    [HttpGet]
    public async Task<IActionResult> Diff(string key, int from, int to)
    {
        var vm = await _getDiff.ExecuteAsync(key, from, to, HttpContext.RequestAborted);
        if (vm == null)
            return NotFound("Prompt version not found.");

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.PromptManage)]
    public async Task<IActionResult> Save(string key, string? content, string? changeNote)
    {
        var result = await _saveVersion.ExecuteAsync(key, content, changeNote, User.Identity?.Name);

        TempData["Error"] = result switch
        {
            SavePromptVersionResult.InvalidInput => "Nội dung prompt không được để trống.",
            SavePromptVersionResult.UnknownPromptKey => "Prompt template không tồn tại dưới /Prompts.",
            _ => null
        };
        TempData["Info"] = result == SavePromptVersionResult.NoChange
            ? "Nội dung không đổi so với bản đang dùng — không tạo phiên bản mới."
            : null;
        if (result == SavePromptVersionResult.Saved)
            TempData["Saved"] = true;

        return result == SavePromptVersionResult.UnknownPromptKey
            ? RedirectToAction(nameof(Index))
            : RedirectToAction(nameof(Detail), new { key });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.PromptManage)]
    public async Task<IActionResult> Activate(Guid id)
    {
        var promptKey = await _activateVersion.ExecuteAsync(id);
        if (promptKey == null)
            return NotFound("Prompt version not found.");

        TempData["Saved"] = true;
        return RedirectToAction(nameof(Detail), new { key = promptKey });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.PromptManage)]
    public async Task<IActionResult> RevertToFile(string key)
    {
        await _revertToFile.ExecuteAsync(key);
        TempData["Saved"] = true;
        return RedirectToAction(nameof(Detail), new { key });
    }
}
