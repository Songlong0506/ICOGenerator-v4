using ICOGenerator.Application.Evals;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Prompt evaluation harness: golden set scenario + run chấm bằng LLM-judge. Mặc định cả controller chỉ
// cần quyền xem; các action thay đổi dữ liệu/đốt token yêu cầu EvalManage.
[RequirePermission(AppPermission.EvalView)]
public class EvalsController : Controller
{
    private readonly GetEvalPageQuery _getEvalPage;
    private readonly CreateEvalScenarioUseCase _createScenario;
    private readonly UpdateEvalScenarioUseCase _updateScenario;
    private readonly DeleteEvalScenarioUseCase _deleteScenario;
    private readonly StartEvalRunUseCase _startRun;
    private readonly GetEvalRunStatusQuery _getRunStatus;
    private readonly GetEvalRunDetailQuery _getRunDetail;
    private readonly CompareEvalRunsQuery _compareRuns;

    public EvalsController(
        GetEvalPageQuery getEvalPage,
        CreateEvalScenarioUseCase createScenario,
        UpdateEvalScenarioUseCase updateScenario,
        DeleteEvalScenarioUseCase deleteScenario,
        StartEvalRunUseCase startRun,
        GetEvalRunStatusQuery getRunStatus,
        GetEvalRunDetailQuery getRunDetail,
        CompareEvalRunsQuery compareRuns)
    {
        _getEvalPage = getEvalPage;
        _createScenario = createScenario;
        _updateScenario = updateScenario;
        _deleteScenario = deleteScenario;
        _startRun = startRun;
        _getRunStatus = getRunStatus;
        _getRunDetail = getRunDetail;
        _compareRuns = compareRuns;
    }

    public async Task<IActionResult> Index()
    {
        return View(await _getEvalPage.ExecuteAsync(HttpContext.RequestAborted));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.EvalManage)]
    public async Task<IActionResult> CreateScenario(string? name, string? promptKey, string? userInput, string? criteria)
    {
        var result = await _createScenario.ExecuteAsync(name, promptKey, userInput, criteria, User.Identity?.Name);
        SetScenarioResultMessage(result);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.EvalManage)]
    public async Task<IActionResult> UpdateScenario(Guid id, string? name, string? promptKey, string? userInput, string? criteria, bool isActive)
    {
        var result = await _updateScenario.ExecuteAsync(id, name, promptKey, userInput, criteria, isActive);
        SetScenarioResultMessage(result);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.EvalManage)]
    public async Task<IActionResult> DeleteScenario(Guid id)
    {
        await _deleteScenario.ExecuteAsync(id);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.EvalManage)]
    public async Task<IActionResult> StartRun(Guid targetModelId, Guid judgeModelId, string? promptKey, string? note)
    {
        var result = await _startRun.ExecuteAsync(targetModelId, judgeModelId, promptKey, note, User.Identity?.Name);

        TempData["Error"] = result switch
        {
            StartEvalRunResult.TargetModelNotFound => "Model mục tiêu không tồn tại hoặc đã tắt.",
            StartEvalRunResult.JudgeModelNotFound => "Model judge không tồn tại hoặc đã tắt.",
            StartEvalRunResult.NoActiveScenarios => "Không có scenario đang bật nào khớp bộ lọc — thêm/bật scenario trước khi chạy.",
            _ => null
        };
        if (result == StartEvalRunResult.Started)
            TempData["RunStarted"] = true;

        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> RunStatus(Guid id)
    {
        var result = await _getRunStatus.ExecuteAsync(id, HttpContext.RequestAborted);
        if (result == null)
            return NotFound("Run not found.");

        return Json(result);
    }

    [HttpGet]
    public async Task<IActionResult> RunDetail(Guid id)
    {
        var result = await _getRunDetail.ExecuteAsync(id, HttpContext.RequestAborted);
        if (result == null)
            return NotFound("Run not found.");

        return Json(result);
    }

    [HttpGet]
    public async Task<IActionResult> Compare(Guid runA, Guid runB)
    {
        var result = await _compareRuns.ExecuteAsync(runA, runB, HttpContext.RequestAborted);
        if (result == null)
            return NotFound("Run not found.");

        return Json(result);
    }

    private void SetScenarioResultMessage(SaveEvalScenarioResult result)
    {
        TempData["Error"] = result switch
        {
            SaveEvalScenarioResult.InvalidInput => "Vui lòng điền đủ Tên, Prompt, Đầu vào và Tiêu chí chấm.",
            SaveEvalScenarioResult.UnknownPromptKey => "Prompt template không tồn tại dưới /Prompts.",
            SaveEvalScenarioResult.NotFound => "Scenario không tồn tại (có thể đã bị xoá).",
            _ => null
        };
    }
}
