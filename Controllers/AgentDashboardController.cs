using ICOGenerator.Application.Agents;
using ICOGenerator.Application.Projects;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Dashboard agent theo dự án (chỉ đọc) — đi vào từ màn hình Projects nên gắn cùng quyền xem Agents.
// Cổng duyệt/đẩy các bước delivery sống ở đây (xem các action POST bên dưới) và yêu cầu thêm
// quyền DeliveryAdvance: user thường dừng ở POC, chỉ TeamDev/Admin mới đẩy tiếp Architecture/code/test.
[RequirePermission(AppPermission.AgentsView)]
public class AgentDashboardController : Controller
{
    private readonly GetAgentDashboardQuery _getAgentDashboardQuery;
    private readonly GetWorkflowStatusQuery _getWorkflowStatusQuery;
    private readonly GetAgentActivityQuery _getAgentActivityQuery;
    private readonly GetAgentCallLogsQuery _getAgentCallLogsQuery;
    private readonly GetCallLogDetailQuery _getCallLogDetailQuery;
    private readonly GetDocumentPreviewQuery _getDocumentPreviewQuery;
    private readonly ApproveStageUseCase _approveStageUseCase;
    private readonly RejectStageUseCase _rejectStageUseCase;
    private readonly RetryWorkflowUseCase _retryWorkflowUseCase;
    private readonly UpdateDeliveryConfigUseCase _updateDeliveryConfigUseCase;

    public AgentDashboardController(
        GetAgentDashboardQuery getAgentDashboardQuery,
        GetWorkflowStatusQuery getWorkflowStatusQuery,
        GetAgentActivityQuery getAgentActivityQuery,
        GetAgentCallLogsQuery getAgentCallLogsQuery,
        GetCallLogDetailQuery getCallLogDetailQuery,
        GetDocumentPreviewQuery getDocumentPreviewQuery,
        ApproveStageUseCase approveStageUseCase,
        RejectStageUseCase rejectStageUseCase,
        RetryWorkflowUseCase retryWorkflowUseCase,
        UpdateDeliveryConfigUseCase updateDeliveryConfigUseCase)
    {
        _getAgentDashboardQuery = getAgentDashboardQuery;
        _getWorkflowStatusQuery = getWorkflowStatusQuery;
        _getAgentActivityQuery = getAgentActivityQuery;
        _getAgentCallLogsQuery = getAgentCallLogsQuery;
        _getCallLogDetailQuery = getCallLogDetailQuery;
        _getDocumentPreviewQuery = getDocumentPreviewQuery;
        _approveStageUseCase = approveStageUseCase;
        _rejectStageUseCase = rejectStageUseCase;
        _retryWorkflowUseCase = retryWorkflowUseCase;
        _updateDeliveryConfigUseCase = updateDeliveryConfigUseCase;
    }

    public async Task<IActionResult> Index(Guid projectId)
    {
        var result = await _getAgentDashboardQuery.ExecuteAsync(projectId);
        if (result == null)
            return RedirectToAction("Index", "Projects");

        ViewBag.Agents = result.Agents;
        ViewBag.Phases = result.Phases;
        ViewBag.WorkspaceDocuments = result.WorkspaceDocuments;
        ViewBag.TotalTokens = result.TotalTokens;
        ViewBag.TokensByAgent = result.TokensByAgent;
        ViewBag.CallsByAgent = result.CallsByAgent;
        ViewBag.LastActivityByAgent = result.LastActivityByAgent;
        return View(result.Project);
    }

    [HttpGet]
    public async Task<IActionResult> WorkflowStatus(Guid projectId)
    {
        return Json(await _getWorkflowStatusQuery.ExecuteAsync(projectId));
    }

    // Lightweight poll for the dashboard: which agents currently have work in flight.
    [HttpGet]
    public async Task<IActionResult> ActiveAgents(Guid projectId)
    {
        return Json(await _getAgentActivityQuery.GetActiveAgentsAsync(projectId));
    }

    // Live operation feed for one agent's running task — backs the debug popup.
    [HttpGet]
    public async Task<IActionResult> AgentActivity(Guid projectId, Guid agentId, long afterSeq = 0)
    {
        return Json(await _getAgentActivityQuery.GetAgentActivityAsync(projectId, agentId, afterSeq));
    }

    [HttpGet]
    public async Task<IActionResult> AgentCallLogs(Guid projectId, Guid agentId)
    {
        return Json(await _getAgentCallLogsQuery.ExecuteAsync(projectId, agentId));
    }

    [HttpGet]
    public async Task<IActionResult> CallLogDetail(Guid id)
    {
        var result = await _getCallLogDetailQuery.ExecuteAsync(id);
        return result == null ? NotFound() : Json(result);
    }

    [HttpGet]
    public async Task<IActionResult> DocumentPreview(Guid id, Guid projectId, string? path)
    {
        var result = await _getDocumentPreviewQuery.ExecuteAsync(id, projectId, path);
        return result == null ? NotFound() : Json(result);
    }

    // ===== Cổng delivery: chỉ TeamDev/Admin (DeliveryAdvance) mới đẩy được pipeline sau bước POC.
    // Trước đây các action này nằm ở RequirementsController; đã chuyển về đây để gom cổng duyệt cùng
    // chỗ artifact được duyệt (workspace 5 phase) và để redirect quay lại đúng dashboard.

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.DeliveryAdvance)]
    public async Task<IActionResult> ApproveStage(Guid projectId, Guid? runId = null)
    {
        var result = await _approveStageUseCase.ExecuteAsync(projectId, runId);

        TempData["Error"] = result switch
        {
            ApproveStageResult.MissingAgent => "Không tìm thấy agent cho bước kế tiếp. Hãy kiểm tra cấu hình agent.",
            ApproveStageResult.MissingGenerationMode => "Chưa chọn Generation Mode. Hãy điền cấu hình delivery ở Agent Dashboard trước khi đẩy sang bước kiến trúc.",
            ApproveStageResult.MissingGitUrls => "Chưa nhập Backend/Frontend Git. Hãy điền cấu hình delivery ở Agent Dashboard trước khi tạo Pull Request.",
            _ => null
        };

        return RedirectToAction(nameof(Index), new { projectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.DeliveryAdvance)]
    public async Task<IActionResult> RejectStage(Guid projectId, Guid? runId = null)
    {
        await _rejectStageUseCase.ExecuteAsync(projectId, runId);
        return RedirectToAction(nameof(Index), new { projectId });
    }

    // Chạy lại bước đã thất bại (vd POC) mà không Approve lại từ đầu — dùng khi lỗi tạm thời như
    // LLM rớt kết nối. Re-queue đúng task đã hỏng, worker sẽ tiếp tục từ chỗ đó.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.DeliveryAdvance)]
    public async Task<IActionResult> RetryWorkflow(Guid projectId, Guid? runId = null)
    {
        var result = await _retryWorkflowUseCase.ExecuteAsync(projectId, runId);

        if (result == RetryWorkflowResult.NoFailedRun || result == RetryWorkflowResult.NoRetryableTask)
            TempData["Error"] = "Không tìm thấy bước thất bại nào để chạy lại.";

        return RedirectToAction(nameof(Index), new { projectId });
    }

    // Team kỹ thuật điền cấu hình delivery (Generation Mode, Backend/Frontend Git) mà end-user không cần
    // biết lúc tạo project. Cùng quyền DeliveryAdvance với các cổng duyệt vì cùng một nhóm người dùng.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.DeliveryAdvance)]
    public async Task<IActionResult> UpdateDeliveryConfig(UpdateDeliveryConfigVm vm)
    {
        var result = await _updateDeliveryConfigUseCase.ExecuteAsync(vm);

        if (result == UpdateDeliveryConfigResult.ProjectNotFound)
            TempData["Error"] = "Không tìm thấy project để cập nhật cấu hình.";
        else
            TempData["Info"] = "Đã lưu cấu hình delivery.";

        return RedirectToAction(nameof(Index), new { projectId = vm.ProjectId });
    }
}
