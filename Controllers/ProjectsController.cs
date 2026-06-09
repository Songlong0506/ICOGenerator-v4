using ICOGenerator.Application.Projects;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

public class ProjectsController : Controller
{
    private readonly GetProjectListQuery _getProjectListQuery;
    private readonly CreateProjectUseCase _createProjectUseCase;
    private readonly GetMockupFileQuery _getMockupFileQuery;

    public ProjectsController(
        GetProjectListQuery getProjectListQuery,
        CreateProjectUseCase createProjectUseCase,
        GetMockupFileQuery getMockupFileQuery)
    {
        _getProjectListQuery = getProjectListQuery;
        _createProjectUseCase = createProjectUseCase;
        _getMockupFileQuery = getMockupFileQuery;
    }

    public async Task<IActionResult> Index()
    {
        var items = await _getProjectListQuery.ExecuteAsync();
        return View(items);
    }

    [HttpPost]
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

        return PhysicalFile(result.FilePath, "text/html", enableRangeProcessing: true);
    }
}
