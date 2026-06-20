using ICOGenerator.Application.Agents;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

public class ManageAgentController : Controller
{
    private readonly GetAgentDashboardQuery _getAgentDashboardQuery;
    private readonly GetWorkflowStatusQuery _getWorkflowStatusQuery;
    private readonly GetAgentActivityQuery _getAgentActivityQuery;
    private readonly GetAgentCallLogsQuery _getAgentCallLogsQuery;
    private readonly GetCallLogDetailQuery _getCallLogDetailQuery;
    private readonly GetDocumentPreviewQuery _getDocumentPreviewQuery;

    public ManageAgentController(
        GetAgentDashboardQuery getAgentDashboardQuery,
        GetWorkflowStatusQuery getWorkflowStatusQuery,
        GetAgentActivityQuery getAgentActivityQuery,
        GetAgentCallLogsQuery getAgentCallLogsQuery,
        GetCallLogDetailQuery getCallLogDetailQuery,
        GetDocumentPreviewQuery getDocumentPreviewQuery)
    {
        _getAgentDashboardQuery = getAgentDashboardQuery;
        _getWorkflowStatusQuery = getWorkflowStatusQuery;
        _getAgentActivityQuery = getAgentActivityQuery;
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
        ViewBag.TotalTokens = result.TotalTokens;
        ViewBag.TokensByAgent = result.TokensByAgent;
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
}
