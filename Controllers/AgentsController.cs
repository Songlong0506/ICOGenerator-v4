using ICOGenerator.Application.Agents;
using ICOGenerator.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

public class AgentsController : Controller
{
    private readonly GetAgentManagementPageQuery _getAgentManagementPageQuery;
    private readonly UpdateAgentUseCase _updateAgentUseCase;

    public AgentsController(GetAgentManagementPageQuery getAgentManagementPageQuery, UpdateAgentUseCase updateAgentUseCase)
    {
        _getAgentManagementPageQuery = getAgentManagementPageQuery;
        _updateAgentUseCase = updateAgentUseCase;
    }

    public async Task<IActionResult> Index(Guid? id)
    {
        var page = await _getAgentManagementPageQuery.ExecuteAsync(id);
        ViewBag.Selected = page.SelectedAgent;
        ViewBag.Models = page.Models;
        ViewBag.Tools = page.Tools;
        return View(page.Agents);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(AgentEditVm vm)
    {
        if (!await _updateAgentUseCase.ExecuteAsync(vm))
            return NotFound();

        TempData["Success"] = "Agent updated successfully.";
        return RedirectToAction(nameof(Index), new { id = vm.Id });
    }
}
