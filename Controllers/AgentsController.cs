using ICOGenerator.Application.Agents;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

[RequirePermission(AppPermission.AgentsView)]
public class AgentsController : Controller
{
    private readonly GetAgentManagementPageQuery _getAgentManagementPageQuery;
    private readonly UpdateAgentUseCase _updateAgentUseCase;

    public AgentsController(GetAgentManagementPageQuery getAgentManagementPageQuery, UpdateAgentUseCase updateAgentUseCase)
    {
        _getAgentManagementPageQuery = getAgentManagementPageQuery;
        _updateAgentUseCase = updateAgentUseCase;
    }

    public async Task<IActionResult> Index(Guid? id, bool shared = false)
    {
        var page = await _getAgentManagementPageQuery.ExecuteAsync(id, shared);
        ViewBag.Selected = page.SelectedAgent;
        ViewBag.Models = page.Models;
        ViewBag.Tools = page.Tools;
        ViewBag.Prompts = page.Prompts;
        ViewBag.SharedSelected = page.SharedSelected;
        return View(page.Agents);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.AgentsManage)]
    public async Task<IActionResult> Update(AgentEditVm vm)
    {
        switch (await _updateAgentUseCase.ExecuteAsync(vm))
        {
            case UpdateAgentResult.NotFound:
                return NotFound();
            case UpdateAgentResult.ModelRequired:
                TempData["Error"] = "Vui lòng chọn AI model cho agent.";
                return RedirectToAction(nameof(Index), new { id = vm.Id });
            default:
                TempData["Success"] = "Agent updated successfully.";
                return RedirectToAction(nameof(Index), new { id = vm.Id });
        }
    }
}
