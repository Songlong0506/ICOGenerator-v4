using ICOGenerator.Application.Projects;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Xem dự án là quyền cơ bản nhất; mọi action GET trong controller đều yêu cầu ProjectsView.
[RequirePermission(AppPermission.ProjectsView)]
public class ProjectsController : Controller
{
    private readonly GetProjectListQuery _getProjectListQuery;
    private readonly CreateProjectUseCase _createProjectUseCase;
    private readonly GetMockupFileQuery _getMockupFileQuery;
    private readonly GetImplementationSourceQuery _getImplementationSourceQuery;
    private readonly IPermissionService _permissions;

    public ProjectsController(
        GetProjectListQuery getProjectListQuery,
        CreateProjectUseCase createProjectUseCase,
        GetMockupFileQuery getMockupFileQuery,
        GetImplementationSourceQuery getImplementationSourceQuery,
        IPermissionService permissions)
    {
        _getProjectListQuery = getProjectListQuery;
        _createProjectUseCase = createProjectUseCase;
        _getMockupFileQuery = getMockupFileQuery;
        _getImplementationSourceQuery = getImplementationSourceQuery;
        _permissions = permissions;
    }

    public async Task<IActionResult> Index(
        int page = 1,
        int pageSize = GetProjectListQuery.DefaultPageSize,
        string[]? orgUnit = null,
        ProjectStatus? status = null)
    {
        // Admin/TeamDev (quyền ProjectsViewAll) thấy mọi project; User thường chỉ thấy project mình tạo.
        var canViewAll = await _permissions.HasPermissionAsync(User, AppPermission.ProjectsViewAll, HttpContext.RequestAborted);
        var result = await _getProjectListQuery.ExecuteAsync(page, pageSize, User.Identity?.Name, canViewAll, orgUnit, status);
        return View(result);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.ProjectsCreate)]
    public async Task<IActionResult> Create(ProjectCreateVm vm)
    {
        if (!ModelState.IsValid)
            return RedirectToAction(nameof(Index));

        await _createProjectUseCase.ExecuteAsync(vm, User.Identity?.Name);
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Mockup(Guid projectId)
    {
        var result = await _getMockupFileQuery.ExecuteAsync(projectId);
        if (result == null)
            return NotFound("Mockup file not found.");

        // This HTML is agent/LLM-generated and served from our own origin. Sandbox it so any injected
        // <script> runs in an opaque origin — no access to the admin auth cookie and no authenticated
        // same-origin POSTs (e.g. to Settings) — closing the prompt-injection escalation path.
        // 'allow-scripts' keeps the demo interactive; 'allow-forms'/'allow-modals' let the POC CRUD
        // submit forms and use confirm()/alert() dialogs. 'allow-same-origin' is deliberately omitted —
        // that omission (opaque origin, no auth cookie, no authenticated same-origin POSTs) is the
        // actual security boundary, and forms/modals don't weaken it.
        Response.Headers["Content-Security-Policy"] = "sandbox allow-scripts allow-forms allow-modals;";
        return PhysicalFile(result.FilePath, "text/html", enableRangeProcessing: true);
    }

    // Packages the agent-generated multi-file app (04_Implementation/src) into a .zip the user can
    // download — the only way to actually get the produced source out of the workspace.
    public async Task<IActionResult> DownloadSource(Guid projectId)
    {
        var result = await _getImplementationSourceQuery.ExecuteAsync(projectId);
        if (result == null)
            return NotFound("Chưa có source code để tải. Hãy chạy tới bước Implementation để agent sinh code trong 04_Implementation/src.");

        // DeleteOnClose removes the temp zip once the response has streamed; FileStreamResult
        // disposes the handle, which triggers that cleanup.
        var stream = new FileStream(
            result.ZipFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        return File(stream, "application/zip", result.DownloadFileName);
    }
}
