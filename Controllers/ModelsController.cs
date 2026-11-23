using ICOGenerator.Localization;
using ICOGenerator.Application.Models;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

[RequirePermission(AppPermission.ModelsView)]
public class ModelsController : Controller
{
    private readonly ListAiModelsQuery _listAiModelsQuery;
    private readonly CreateAiModelUseCase _createAiModelUseCase;
    private readonly UpdateAiModelUseCase _updateAiModelUseCase;
    private readonly DeleteAiModelUseCase _deleteAiModelUseCase;
    private readonly TestAiModelConnectionUseCase _testAiModelConnectionUseCase;
    private readonly GetModelSlotsQuery _getModelSlotsQuery;
    private readonly UpdateModelSlotUseCase _updateModelSlotUseCase;

    public ModelsController(
        ListAiModelsQuery listAiModelsQuery,
        CreateAiModelUseCase createAiModelUseCase,
        UpdateAiModelUseCase updateAiModelUseCase,
        DeleteAiModelUseCase deleteAiModelUseCase,
        TestAiModelConnectionUseCase testAiModelConnectionUseCase,
        GetModelSlotsQuery getModelSlotsQuery,
        UpdateModelSlotUseCase updateModelSlotUseCase)
    {
        _listAiModelsQuery = listAiModelsQuery;
        _createAiModelUseCase = createAiModelUseCase;
        _updateAiModelUseCase = updateAiModelUseCase;
        _deleteAiModelUseCase = deleteAiModelUseCase;
        _testAiModelConnectionUseCase = testAiModelConnectionUseCase;
        _getModelSlotsQuery = getModelSlotsQuery;
        _updateModelSlotUseCase = updateModelSlotUseCase;
    }

    public async Task<IActionResult> Index(int page = 1, int pageSize = ListAiModelsQuery.DefaultPageSize)
    {
        ViewBag.ModelSlots = await _getModelSlotsQuery.ExecuteAsync(HttpContext.RequestAborted);
        return View(await _listAiModelsQuery.ExecuteAsync(page, pageSize));
    }

    /// <summary>
    /// Gán model cho một suất ở khối "Model theo vai trò". <paramref name="aiModelId"/> trống = bỏ gán, lời gọi
    /// xin suất đó chạy model của agent gọi nó.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.ModelsEdit)]
    public async Task<IActionResult> UpdateSlot(ModelSlot slot, Guid? aiModelId)
    {
        var result = await _updateModelSlotUseCase.ExecuteAsync(slot, aiModelId, User.Identity?.Name);
        switch (result)
        {
            case UpdateModelSlotResult.Saved:
                TempData["Success"] = Loc.T("ModelsController.UpdateSlot", slot.GetTitle());
                break;
            case UpdateModelSlotResult.ModelUnavailable:
                TempData["Error"] = Loc.T("ModelsController.UpdateSlot.2");
                break;
            default:
                TempData["Error"] = Loc.T("ModelsController.UpdateSlot.3");
                break;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.ModelsCreate)]
    public async Task<IActionResult> Create(AiModel model)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = Loc.T("ModelsController.Create");
            return RedirectToAction(nameof(Index));
        }

        await _createAiModelUseCase.ExecuteAsync(model, User.Identity?.Name);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.ModelsEdit)]
    public async Task<IActionResult> Update(AiModel input)
    {
        // Trên form Edit, để trống ApiKey nghĩa là "giữ key hiện tại" (xem UpdateAiModelUseCase): key
        // thật không bao giờ được gửi về browser nên trường luôn rỗng khi chỉ sửa các thuộc tính khác.
        // Với <Nullable>enable</Nullable>, string không-nullable như ApiKey bị coi là [Required] ngầm định,
        // khiến ApiKey rỗng làm ModelState invalid. Bỏ riêng lỗi validation của ApiKey để cho phép giữ key.
        ModelState.Remove(nameof(AiModel.ApiKey));

        if (!ModelState.IsValid)
        {
            TempData["Error"] = Loc.T("ModelsController.Update");
            return RedirectToAction(nameof(Index));
        }

        if (!await _updateAiModelUseCase.ExecuteAsync(input))
            TempData["Error"] = Loc.T("ModelsController.Update.2");

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Gọi thử endpoint đang gõ trong modal Add/Edit (nút "Test Connection") và trả JSON cho JS hiển thị.
    /// Nhận cùng form như Create/Update nên ApiKey để trống vẫn hợp lệ (nghĩa là "dùng key đã lưu" — xem
    /// <see cref="TestAiModelConnectionUseCase"/>). Mở cho cả quyền tạo lẫn quyền sửa vì nút có ở cả hai modal.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.ModelsCreate, AppPermission.ModelsEdit)]
    public async Task<IActionResult> TestConnection(AiModel input, CancellationToken cancellationToken)
    {
        ModelState.Remove(nameof(AiModel.ApiKey));

        if (!ModelState.IsValid)
            return Json(new TestAiModelConnectionResult(false, Loc.T("ModelsController.TestConnection")));

        return Json(await _testAiModelConnectionUseCase.ExecuteAsync(input, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.ModelsDelete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await _deleteAiModelUseCase.ExecuteAsync(id);
        if (result == DeleteAiModelResult.InUse)
            TempData["Error"] = Loc.T("ModelsController.Delete");

        return RedirectToAction(nameof(Index));
    }
}
