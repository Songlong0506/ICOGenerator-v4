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
    private readonly GetLearnedChecklistQuery _getLearnedChecklistQuery;
    private readonly UpdateLearnedChecklistUseCase _updateLearnedChecklistUseCase;
    private readonly IPermissionService _permissions;

    public AgentsController(
        GetAgentManagementPageQuery getAgentManagementPageQuery,
        UpdateAgentUseCase updateAgentUseCase,
        GetLearnedChecklistQuery getLearnedChecklistQuery,
        UpdateLearnedChecklistUseCase updateLearnedChecklistUseCase,
        IPermissionService permissions)
    {
        _getAgentManagementPageQuery = getAgentManagementPageQuery;
        _updateAgentUseCase = updateAgentUseCase;
        _getLearnedChecklistQuery = getLearnedChecklistQuery;
        _updateLearnedChecklistUseCase = updateLearnedChecklistUseCase;
        _permissions = permissions;
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

    // ==== Checklist BA tự học được (từ hội thoại + ghi chú POC của các dự án trước) ====
    // Nội dung này được nạp vào prompt ở MỌI lượt chat của các dự án cùng miền, nhưng trước đây không có
    // màn hình nào xem được — một bài học rút sai từ một dự án cá biệt cứ thế làm nhiễu mọi dự án sau mà
    // không ai biết để gỡ. Trang này là chỗ xem/sửa/gỡ nó.
    public async Task<IActionResult> Checklist()
    {
        // Người chỉ có quyền XEM vẫn vào được trang này (thấy BA đang được dạy gì là thông tin hữu ích),
        // nhưng không thấy form sửa — POST đã bị chặn bằng AgentsManage nên hiện form chỉ để bấm rồi bị từ chối.
        ViewBag.CanManage = await _permissions.HasPermissionAsync(User, AppPermission.AgentsManage, HttpContext.RequestAborted);
        return View(await _getLearnedChecklistQuery.ExecuteAsync(HttpContext.RequestAborted));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.AgentsManage)]
    public async Task<IActionResult> SaveChecklist(string? domainKey, string? notes)
    {
        var result = await _updateLearnedChecklistUseCase.ExecuteAsync(domainKey, notes, HttpContext.RequestAborted);
        if (result == UpdateLearnedChecklistResult.BaNotConfigured)
            TempData["Error"] = "Chưa cấu hình agent BA.";
        else
            TempData["Success"] = string.IsNullOrWhiteSpace(notes)
                ? "Đã gỡ toàn bộ checklist của nhóm này."
                : "Đã lưu checklist.";

        return RedirectToAction(nameof(Checklist));
    }
}
