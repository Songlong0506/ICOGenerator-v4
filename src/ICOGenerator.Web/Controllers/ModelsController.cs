using ICOGenerator.Application.Models;
using ICOGenerator.Domain;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

public class ModelsController : Controller
{
    private readonly ListAiModelsQuery _listAiModelsQuery;
    private readonly CreateAiModelUseCase _createAiModelUseCase;
    private readonly UpdateAiModelUseCase _updateAiModelUseCase;
    private readonly SetDefaultAiModelUseCase _setDefaultAiModelUseCase;
    private readonly DeleteAiModelUseCase _deleteAiModelUseCase;

    public ModelsController(
        ListAiModelsQuery listAiModelsQuery,
        CreateAiModelUseCase createAiModelUseCase,
        UpdateAiModelUseCase updateAiModelUseCase,
        SetDefaultAiModelUseCase setDefaultAiModelUseCase,
        DeleteAiModelUseCase deleteAiModelUseCase)
    {
        _listAiModelsQuery = listAiModelsQuery;
        _createAiModelUseCase = createAiModelUseCase;
        _updateAiModelUseCase = updateAiModelUseCase;
        _setDefaultAiModelUseCase = setDefaultAiModelUseCase;
        _deleteAiModelUseCase = deleteAiModelUseCase;
    }

    public async Task<IActionResult> Index()
    {
        return View(await _listAiModelsQuery.ExecuteAsync());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AiModel model)
    {
        await _createAiModelUseCase.ExecuteAsync(model);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(AiModel input)
    {
        return await _updateAiModelUseCase.ExecuteAsync(input)
            ? RedirectToAction(nameof(Index))
            : NotFound();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefault(Guid id)
    {
        await _setDefaultAiModelUseCase.ExecuteAsync(id);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await _deleteAiModelUseCase.ExecuteAsync(id);
        if (result == DeleteAiModelResult.InUse)
            TempData["Error"] = "Model đang được Agent sử dụng, không thể xóa.";

        return RedirectToAction(nameof(Index));
    }
}
