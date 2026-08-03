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
    private readonly CancelEvalRunUseCase _cancelRun;
    private readonly DeleteEvalRunUseCase _deleteRun;
    private readonly GetEvalRunStatusQuery _getRunStatus;
    private readonly GetEvalRunDetailQuery _getRunDetail;
    private readonly CompareEvalRunsQuery _compareRuns;

    public EvalsController(
        GetEvalPageQuery getEvalPage,
        CreateEvalScenarioUseCase createScenario,
        UpdateEvalScenarioUseCase updateScenario,
        DeleteEvalScenarioUseCase deleteScenario,
        StartEvalRunUseCase startRun,
        CancelEvalRunUseCase cancelRun,
        DeleteEvalRunUseCase deleteRun,
        GetEvalRunStatusQuery getRunStatus,
        GetEvalRunDetailQuery getRunDetail,
        CompareEvalRunsQuery compareRuns)
    {
        _getEvalPage = getEvalPage;
        _createScenario = createScenario;
        _updateScenario = updateScenario;
        _deleteScenario = deleteScenario;
        _startRun = startRun;
        _cancelRun = cancelRun;
        _deleteRun = deleteRun;
        _getRunStatus = getRunStatus;
        _getRunDetail = getRunDetail;
        _compareRuns = compareRuns;
    }

    public async Task<IActionResult> Index(
        string? runPromptKey = null,
        EvalRunStatus? runStatus = null,
        int page = 1,
        int pageSize = GetEvalPageQuery.DefaultPageSize)
    {
        return View(await _getEvalPage.ExecuteAsync(runPromptKey, runStatus, page, pageSize, HttpContext.RequestAborted));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.EvalManage)]
    public async Task<IActionResult> CreateScenario(string? name, string? promptKey, string? userInput, string? criteria, EvalScenarioKind kind = EvalScenarioKind.Prompt)
    {
        var result = await _createScenario.ExecuteAsync(name, promptKey, userInput, criteria, User.Identity?.Name, kind);
        SetScenarioResultMessage(result);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.EvalManage)]
    public async Task<IActionResult> UpdateScenario(Guid id, string? name, string? promptKey, string? userInput, string? criteria, bool isActive, EvalScenarioKind kind = EvalScenarioKind.Prompt)
    {
        var result = await _updateScenario.ExecuteAsync(id, name, promptKey, userInput, criteria, isActive, kind);
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
            StartEvalRunResult.DuplicateRunInProgress => "Đã có một run y hệt đang chờ/đang chạy — chờ nó xong (hoặc huỷ nó) thay vì trả tiền hai lần cho cùng một phép đo.",
            _ => null
        };
        if (result == StartEvalRunResult.Started)
            TempData["RunStarted"] = true;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.EvalManage)]
    public async Task<IActionResult> CancelRun(Guid id, string? runPromptKey, EvalRunStatus? runStatus, int page = 1)
    {
        var result = await _cancelRun.ExecuteAsync(id, HttpContext.RequestAborted);

        TempData["Error"] = result switch
        {
            CancelEvalRunResult.NotFound => "Run không tồn tại (có thể đã bị xoá).",
            CancelEvalRunResult.AlreadyFinished => "Run đã kết thúc — không còn gì để huỷ.",
            _ => null
        };
        TempData["Info"] = result switch
        {
            CancelEvalRunResult.Cancelled => "Đã huỷ run trước khi nó chạy scenario nào.",
            CancelEvalRunResult.CancelRequested => "Đã gửi yêu cầu huỷ — run dừng sau khi xong scenario đang chạy dở.",
            _ => null
        };

        return RedirectToAction(nameof(Index), new { runPromptKey, runStatus, page });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.EvalManage)]
    public async Task<IActionResult> DeleteRun(Guid id, string? runPromptKey, EvalRunStatus? runStatus, int page = 1)
    {
        var result = await _deleteRun.ExecuteAsync(id, HttpContext.RequestAborted);

        TempData["Error"] = result switch
        {
            DeleteEvalRunResult.NotFound => "Run không tồn tại (có thể đã bị xoá).",
            DeleteEvalRunResult.StillRunning => "Run đang chờ/đang chạy — huỷ nó trước rồi mới xoá được.",
            _ => null
        };

        return RedirectToAction(nameof(Index), new { runPromptKey, runStatus, page });
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
