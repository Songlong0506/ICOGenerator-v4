using ICOGenerator.Application.Projects;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

public class ProjectsController : Controller
{
    private readonly GetProjectListQuery _getProjectListQuery;
    private readonly CreateProjectUseCase _createProjectUseCase;
    private readonly GetMockupFileQuery _getMockupFileQuery;
    private readonly GetProjectDeliverablesQuery _getProjectDeliverablesQuery;
    private readonly GetDeliverableFileQuery _getDeliverableFileQuery;
    private readonly GetImplementationSourceQuery _getImplementationSourceQuery;

    public ProjectsController(
        GetProjectListQuery getProjectListQuery,
        CreateProjectUseCase createProjectUseCase,
        GetMockupFileQuery getMockupFileQuery,
        GetProjectDeliverablesQuery getProjectDeliverablesQuery,
        GetDeliverableFileQuery getDeliverableFileQuery,
        GetImplementationSourceQuery getImplementationSourceQuery)
    {
        _getProjectListQuery = getProjectListQuery;
        _createProjectUseCase = createProjectUseCase;
        _getMockupFileQuery = getMockupFileQuery;
        _getProjectDeliverablesQuery = getProjectDeliverablesQuery;
        _getDeliverableFileQuery = getDeliverableFileQuery;
        _getImplementationSourceQuery = getImplementationSourceQuery;
    }

    public async Task<IActionResult> Index(int page = 1, int pageSize = GetProjectListQuery.DefaultPageSize)
    {
        var result = await _getProjectListQuery.ExecuteAsync(page, pageSize);
        return View(result);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ProjectCreateVm vm)
    {
        if (!ModelState.IsValid)
            return RedirectToAction(nameof(Index));

        await _createProjectUseCase.ExecuteAsync(vm);
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
        // 'allow-scripts' keeps the demo interactive; 'allow-same-origin' is deliberately omitted.
        Response.Headers["Content-Security-Policy"] = "sandbox allow-scripts;";
        return PhysicalFile(result.FilePath, "text/html", enableRangeProcessing: true);
    }

    public async Task<IActionResult> Deliverables(Guid projectId)
    {
        var result = await _getProjectDeliverablesQuery.ExecuteAsync(projectId);
        if (result == null)
            return RedirectToAction(nameof(Index));

        return View(result);
    }

    [HttpGet]
    public async Task<IActionResult> DeliverableFile(Guid projectId, string path, bool download = false)
    {
        var file = await _getDeliverableFileQuery.ExecuteAsync(projectId, path);
        if (file == null)
            return NotFound("Deliverable not found.");

        // Nội dung do agent/LLM sinh ra, phục vụ từ chính origin của ta. Chặn thực thi/sniff:
        // nosniff để trình duyệt không "đoán" thành HTML; sandbox CSP để kể cả khi bị coi là HTML
        // thì script cũng không chạy (không allow-scripts). File văn bản xem trực tiếp dạng text/plain;
        // mọi loại khác buộc tải về (attachment) thay vì render trong origin.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "sandbox;";

        if (file.TextPreviewable && !download)
            return PhysicalFile(file.FilePath, "text/plain; charset=utf-8");

        return PhysicalFile(file.FilePath, "application/octet-stream", file.FileName);
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
