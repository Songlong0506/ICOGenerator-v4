using ICOGenerator.Application.Agents;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

public class ManageAgentController : Controller
{
    private readonly GetAgentDashboardQuery _getAgentDashboardQuery;
    private readonly GetAgentCallLogsQuery _getAgentCallLogsQuery;
    private readonly GetCallLogDetailQuery _getCallLogDetailQuery;

    public ManageAgentController(
        GetAgentDashboardQuery getAgentDashboardQuery,
        GetAgentCallLogsQuery getAgentCallLogsQuery,
        GetCallLogDetailQuery getCallLogDetailQuery)
    {
        _getAgentDashboardQuery = getAgentDashboardQuery;
        _getAgentCallLogsQuery = getAgentCallLogsQuery;
        _getCallLogDetailQuery = getCallLogDetailQuery;
    }

    public async Task<IActionResult> Index(Guid projectId)
    {
        var result = await _getAgentDashboardQuery.ExecuteAsync(projectId);
        if (result == null)
            return RedirectToAction("Index", "Projects");

        ViewBag.Agents = result.Agents;
        ViewBag.Phases = result.Phases;
        return View(result.Project);
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
}
