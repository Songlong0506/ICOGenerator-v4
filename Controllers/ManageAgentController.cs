using ICOGenerator.Application.Agents;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

public class ManageAgentController : Controller
{
    private readonly GetAgentDashboardQuery _getAgentDashboardQuery;
    private readonly GetWorkflowStatusQuery _getWorkflowStatusQuery;
    private readonly GetAgentCallLogsQuery _getAgentCallLogsQuery;
    private readonly GetCallLogDetailQuery _getCallLogDetailQuery;
    private readonly GetDocumentPreviewQuery _getDocumentPreviewQuery;

    public ManageAgentController(
        GetAgentDashboardQuery getAgentDashboardQuery,
        GetWorkflowStatusQuery getWorkflowStatusQuery,
        GetAgentCallLogsQuery getAgentCallLogsQuery,
        GetCallLogDetailQuery getCallLogDetailQuery,
        GetDocumentPreviewQuery getDocumentPreviewQuery)
    {
        _getAgentDashboardQuery = getAgentDashboardQuery;
        _getWorkflowStatusQuery = getWorkflowStatusQuery;
        _getAgentCallLogsQuery = getAgentCallLogsQuery;
        _getCallLogDetailQuery = getCallLogDetailQuery;
        _getDocumentPreviewQuery = getDocumentPreviewQuery;
    }

    public async Task<IActionResult> Index(Guid projectId)
    {
        var result = await _getAgentDashboardQuery.ExecuteAsync(projectId);
        if (result == null)
            return RedirectToAction("Index", "Projects");

        ViewBag.Agents = result.Agents;
        ViewBag.Phases = result.Phases;
        ViewBag.WorkspaceDocuments = result.WorkspaceDocuments;
        return View(result.Project);
    }

    [HttpGet]
    public async Task<IActionResult> WorkflowStatus(Guid projectId)
    {
        return Json(await _getWorkflowStatusQuery.ExecuteAsync(projectId));
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
    public async Task<IActionResult> DocumentPreview(Guid id)
    {
        var result = await _getDocumentPreviewQuery.ExecuteAsync(id);
        return result == null ? NotFound() : Json(result);
    }
}
