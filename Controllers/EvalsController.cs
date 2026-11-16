using ICOGenerator.Localization;
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
    private readonly GetJudgeAuditPageQuery _getJudgeAuditPage;
    private readonly GetJudgeAuditDetailQuery _getJudgeAuditDetail;
    private readonly StartJudgeAuditUseCase _startJudgeAudit;
    private readonly CancelJudgeAuditUseCase _cancelJudgeAudit;

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
        CompareEvalRunsQuery compareRuns,
        GetJudgeAuditPageQuery getJudgeAuditPage,
        GetJudgeAuditDetailQuery getJudgeAuditDetail,
        StartJudgeAuditUseCase startJudgeAudit,
        CancelJudgeAuditUseCase cancelJudgeAudit)
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
        _getJudgeAuditPage = getJudgeAuditPage;
        _getJudgeAuditDetail = getJudgeAuditDetail;
        _startJudgeAudit = startJudgeAudit;
        _cancelJudgeAudit = cancelJudgeAudit;
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
            StartEvalRunResult.TargetModelNotFound => Loc.T("EvalsController.StartRun"),
            StartEvalRunResult.JudgeModelNotFound => Loc.T("EvalsController.StartRun.2"),
            StartEvalRunResult.NoActiveScenarios => Loc.T("EvalsController.StartRun.3"),
            StartEvalRunResult.DuplicateRunInProgress => Loc.T("EvalsController.StartRun.4"),
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
            CancelEvalRunResult.NotFound => Loc.T("EvalsController.CancelRun"),
            CancelEvalRunResult.AlreadyFinished => Loc.T("EvalsController.CancelRun.2"),
            _ => null
        };
        TempData["Info"] = result switch
        {
            CancelEvalRunResult.Cancelled => Loc.T("EvalsController.CancelRun.3"),
            CancelEvalRunResult.CancelRequested => Loc.T("EvalsController.CancelRun.4"),
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
            DeleteEvalRunResult.NotFound => Loc.T("EvalsController.DeleteRun"),
            DeleteEvalRunResult.StillRunning => Loc.T("EvalsController.DeleteRun.2"),
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

    // ── Kiểm toán trọng tài ─────────────────────────────────────────────────────────────────────────
    // Trang kết quả hiện lại ĐẦU VÀO của lượt trọng tài — trích hội thoại của mọi project — nên cả bốn action
    // đòi thêm ProjectsViewAll ngoài quyền Eval: quyền xem eval không phải quyền đọc chat của project khác.

    [HttpGet]
    [RequirePermission(AppPermission.ProjectsViewAll)]
    public async Task<IActionResult> JudgeAudits()
    {
        return View(await _getJudgeAuditPage.ExecuteAsync(HttpContext.RequestAborted));
    }

    [HttpGet]
    [RequirePermission(AppPermission.ProjectsViewAll)]
    public async Task<IActionResult> JudgeAudit(Guid id)
    {
        var result = await _getJudgeAuditDetail.ExecuteAsync(id, HttpContext.RequestAborted);
        if (result == null)
            return NotFound("Judge audit not found.");

        return View(result);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.EvalManage)]
    [RequirePermission(AppPermission.ProjectsViewAll)]
    public async Task<IActionResult> StartJudgeAudit(
        string? purpose, string? sourceModelId, DateTime? from, DateTime? to, int sampleSize,
        Guid? candidateModelId, Guid arbiterModelId, bool arbitrateAll, string? note)
    {
        var result = await _startJudgeAudit.ExecuteAsync(
            new StartJudgeAuditInput(purpose, sourceModelId, from, to, sampleSize, candidateModelId, arbiterModelId, arbitrateAll, note),
            User.Identity?.Name, HttpContext.RequestAborted);

        TempData["Error"] = result switch
        {
            StartJudgeAuditResult.UnknownPurpose => Loc.T("EvalsController.StartJudgeAudit"),
            StartJudgeAuditResult.ArbiterModelNotFound => Loc.T("EvalsController.StartJudgeAudit.2"),
            StartJudgeAuditResult.CandidateModelNotFound => Loc.T("EvalsController.StartJudgeAudit.3"),
            StartJudgeAuditResult.InvalidSampleSize => Loc.T("EvalsController.StartJudgeAudit.4", StartJudgeAuditUseCase.MaxSampleSize),
            StartJudgeAuditResult.NoMatchingLogs => Loc.T("EvalsController.StartJudgeAudit.5"),
            StartJudgeAuditResult.DuplicateRunInProgress => Loc.T("EvalsController.StartJudgeAudit.6"),
            _ => null
        };
        if (result == StartJudgeAuditResult.Started)
            TempData["Info"] = Loc.T("EvalsController.StartJudgeAudit.7");

        return RedirectToAction(nameof(JudgeAudits));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.EvalManage)]
    [RequirePermission(AppPermission.ProjectsViewAll)]
    public async Task<IActionResult> CancelJudgeAudit(Guid id)
    {
        var result = await _cancelJudgeAudit.ExecuteAsync(id, HttpContext.RequestAborted);

        TempData["Error"] = result switch
        {
            CancelEvalRunResult.NotFound => Loc.T("EvalsController.CancelJudgeAudit"),
            CancelEvalRunResult.AlreadyFinished => Loc.T("EvalsController.CancelJudgeAudit.2"),
            _ => null
        };
        TempData["Info"] = result switch
        {
            CancelEvalRunResult.Cancelled => Loc.T("EvalsController.CancelJudgeAudit.3"),
            CancelEvalRunResult.CancelRequested => Loc.T("EvalsController.CancelJudgeAudit.4"),
            _ => null
        };

        return RedirectToAction(nameof(JudgeAudits));
    }

    private void SetScenarioResultMessage(SaveEvalScenarioResult result)
    {
        TempData["Error"] = result switch
        {
            SaveEvalScenarioResult.InvalidInput => Loc.T("EvalsController.SetScenarioResultMessage"),
            SaveEvalScenarioResult.UnknownPromptKey => Loc.T("EvalsController.SetScenarioResultMessage.2"),
            SaveEvalScenarioResult.NotFound => Loc.T("EvalsController.SetScenarioResultMessage.3"),
            _ => null
        };
    }
}
