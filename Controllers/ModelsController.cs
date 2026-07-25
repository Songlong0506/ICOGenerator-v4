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

    public ModelsController(
        ListAiModelsQuery listAiModelsQuery,
        CreateAiModelUseCase createAiModelUseCase,
        UpdateAiModelUseCase updateAiModelUseCase,
        DeleteAiModelUseCase deleteAiModelUseCase,
        TestAiModelConnectionUseCase testAiModelConnectionUseCase)
    {
        _listAiModelsQuery = listAiModelsQuery;
        _createAiModelUseCase = createAiModelUseCase;
        _updateAiModelUseCase = updateAiModelUseCase;
        _deleteAiModelUseCase = deleteAiModelUseCase;
        _testAiModelConnectionUseCase = testAiModelConnectionUseCase;
    }

    public async Task<IActionResult> Index(int page = 1, int pageSize = ListAiModelsQuery.DefaultPageSize)
    {
        return View(await _listAiModelsQuery.ExecuteAsync(page, pageSize));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.ModelsCreate)]
    public async Task<IActionResult> Create(AiModel model)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Dữ liệu model không hợp lệ. Vui lòng kiểm tra lại.";
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
            TempData["Error"] = "Dữ liệu model không hợp lệ. Vui lòng kiểm tra lại.";
            return RedirectToAction(nameof(Index));
        }

        if (!await _updateAiModelUseCase.ExecuteAsync(input))
            TempData["Error"] = "Model không tồn tại.";

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
            return Json(new TestAiModelConnectionResult(false, "Dữ liệu model không hợp lệ. Vui lòng kiểm tra lại."));

        return Json(await _testAiModelConnectionUseCase.ExecuteAsync(input, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.ModelsDelete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await _deleteAiModelUseCase.ExecuteAsync(id);
        if (result == DeleteAiModelResult.InUse)
            TempData["Error"] = "Model đang được Agent sử dụng, không thể xóa.";

        return RedirectToAction(nameof(Index));
    }
}
