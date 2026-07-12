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
    private readonly GetProjectMembersQuery _getProjectMembersQuery;
    private readonly AddProjectMemberUseCase _addProjectMemberUseCase;
    private readonly RemoveProjectMemberUseCase _removeProjectMemberUseCase;
    private readonly GetPocReviewPageQuery _getPocReviewPageQuery;
    private readonly GetAnnotatableMockupQuery _getAnnotatableMockupQuery;
    private readonly GetPocAnnotationsQuery _getPocAnnotationsQuery;
    private readonly AddPocAnnotationUseCase _addPocAnnotationUseCase;
    private readonly DeletePocAnnotationUseCase _deletePocAnnotationUseCase;
    private readonly SubmitPocAnnotationsUseCase _submitPocAnnotationsUseCase;
    private readonly ApplyPocAnnotationsRevisionUseCase _applyPocAnnotationsRevisionUseCase;
    private readonly IPermissionService _permissions;

    public ProjectsController(
        GetProjectListQuery getProjectListQuery,
        CreateProjectUseCase createProjectUseCase,
        GetMockupFileQuery getMockupFileQuery,
        GetImplementationSourceQuery getImplementationSourceQuery,
        GetProjectMembersQuery getProjectMembersQuery,
        AddProjectMemberUseCase addProjectMemberUseCase,
        RemoveProjectMemberUseCase removeProjectMemberUseCase,
        GetPocReviewPageQuery getPocReviewPageQuery,
        GetAnnotatableMockupQuery getAnnotatableMockupQuery,
        GetPocAnnotationsQuery getPocAnnotationsQuery,
        AddPocAnnotationUseCase addPocAnnotationUseCase,
        DeletePocAnnotationUseCase deletePocAnnotationUseCase,
        SubmitPocAnnotationsUseCase submitPocAnnotationsUseCase,
        ApplyPocAnnotationsRevisionUseCase applyPocAnnotationsRevisionUseCase,
        IPermissionService permissions)
    {
        _getProjectListQuery = getProjectListQuery;
        _createProjectUseCase = createProjectUseCase;
        _getMockupFileQuery = getMockupFileQuery;
        _getImplementationSourceQuery = getImplementationSourceQuery;
        _getProjectMembersQuery = getProjectMembersQuery;
        _addProjectMemberUseCase = addProjectMemberUseCase;
        _removeProjectMemberUseCase = removeProjectMemberUseCase;
        _getPocReviewPageQuery = getPocReviewPageQuery;
        _getAnnotatableMockupQuery = getAnnotatableMockupQuery;
        _getPocAnnotationsQuery = getPocAnnotationsQuery;
        _addPocAnnotationUseCase = addPocAnnotationUseCase;
        _deletePocAnnotationUseCase = deletePocAnnotationUseCase;
        _submitPocAnnotationsUseCase = submitPocAnnotationsUseCase;
        _applyPocAnnotationsRevisionUseCase = applyPocAnnotationsRevisionUseCase;
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

    public async Task<IActionResult> Mockup(Guid projectId, bool annotate = false)
    {
        // This HTML is agent/LLM-generated and served from our own origin. Sandbox it so any injected
        // <script> runs in an opaque origin — no access to the admin auth cookie and no authenticated
        // same-origin POSTs (e.g. to Settings) — closing the prompt-injection escalation path.
        // 'allow-scripts' keeps the demo interactive; 'allow-forms'/'allow-modals' let the POC CRUD
        // submit forms and use confirm()/alert() dialogs. 'allow-same-origin' is deliberately omitted —
        // that omission (opaque origin, no auth cookie, no authenticated same-origin POSTs) is the
        // actual security boundary, and forms/modals don't weaken it.
        Response.Headers["Content-Security-Policy"] = "sandbox allow-scripts allow-forms allow-modals;";

        // Chế độ review (iframe trong trang PocReview): trả mockup ĐÃ TIÊM script annotation — script
        // chạy trong chính sandbox trên, chỉ nói chuyện với trang cha qua postMessage.
        if (annotate)
        {
            var annotatable = await _getAnnotatableMockupQuery.ExecuteAsync(projectId, HttpContext.RequestAborted);
            if (annotatable == null)
                return NotFound("Mockup file not found.");

            return Content(annotatable.Html, "text/html");
        }

        var result = await _getMockupFileQuery.ExecuteAsync(projectId);
        if (result == null)
            return NotFound("Mockup file not found.");

        return PhysicalFile(result.FilePath, "text/html", enableRangeProcessing: true);
    }

    // ----- POC Review: xem POC trong khung + annotate trực tiếp lên giao diện. -----

    public async Task<IActionResult> PocReview(Guid projectId)
    {
        var result = await _getPocReviewPageQuery.ExecuteAsync(projectId, HttpContext.RequestAborted);
        if (result == null)
            return RedirectToAction(nameof(Index));

        ViewBag.HasMockup = result.HasMockup;
        ViewBag.CanComment = await _permissions.HasPermissionAsync(User, AppPermission.RequirementsComment, HttpContext.RequestAborted);
        ViewBag.CanAdvance = await _permissions.HasPermissionAsync(User, AppPermission.DeliveryAdvance, HttpContext.RequestAborted);
        return View(result.Project);
    }

    [HttpGet]
    public async Task<IActionResult> PocAnnotations(Guid projectId)
    {
        var result = await _getPocAnnotationsQuery.ExecuteAsync(projectId, User.Identity?.Name, HttpContext.RequestAborted);
        return result == null ? NotFound() : Json(result);
    }

    [HttpPost]
    [RequirePermission(AppPermission.RequirementsComment)]
    public async Task<IActionResult> AddPocAnnotation(Guid projectId, string? elementLabel, string? elementPath, string? comment)
    {
        var result = await _addPocAnnotationUseCase.ExecuteAsync(
            projectId, elementLabel, elementPath, comment, User.Identity?.Name, HttpContext.RequestAborted);

        return Json(new
        {
            ok = result == AddPocAnnotationResult.Added,
            message = result switch
            {
                AddPocAnnotationResult.MissingComment => "Hãy nhập nhận xét cho phần tử đã chọn.",
                AddPocAnnotationResult.ProjectNotFound => "Không tìm thấy project.",
                _ => null
            }
        });
    }

    [HttpPost]
    [RequirePermission(AppPermission.RequirementsComment)]
    public async Task<IActionResult> DeletePocAnnotation(Guid id)
    {
        var result = await _deletePocAnnotationUseCase.ExecuteAsync(id, User.Identity?.Name, HttpContext.RequestAborted);

        return Json(new
        {
            ok = result == DeletePocAnnotationResult.Deleted,
            message = result == DeletePocAnnotationResult.NotAllowed
                ? "Chỉ xóa được góp ý của chính bạn khi nó chưa được gửi đi."
                : null
        });
    }

    // "Gửi phản hồi cho đội Dev": chuyển các annotation Open sang Submitted + thông báo người có quyền
    // DeliveryAdvance. Không đụng tới workflow — đội Dev quyết định lúc nào biến góp ý thành yêu cầu chỉnh sửa.
    [HttpPost]
    [RequirePermission(AppPermission.RequirementsComment)]
    public async Task<IActionResult> SubmitPocAnnotations(Guid projectId)
    {
        var result = await _submitPocAnnotationsUseCase.ExecuteAsync(projectId, User.Identity?.Name, HttpContext.RequestAborted);

        return Json(new
        {
            ok = result == SubmitPocAnnotationsResult.Submitted,
            message = result switch
            {
                SubmitPocAnnotationsResult.Submitted => "Đã gửi góp ý cho đội Dev.",
                SubmitPocAnnotationsResult.NothingToSubmit => "Không có góp ý mới nào để gửi.",
                _ => "Không tìm thấy project."
            }
        });
    }

    // Đội Dev (DeliveryAdvance) gom mọi góp ý chưa xử lý thành MỘT yêu cầu chỉnh sửa POC qua đúng cơ chế
    // cổng duyệt sẵn có — agent sửa lại POC theo từng mục.
    [HttpPost]
    [RequirePermission(AppPermission.DeliveryAdvance)]
    public async Task<IActionResult> ApplyPocAnnotationsRevision(Guid projectId)
    {
        var result = await _applyPocAnnotationsRevisionUseCase.ExecuteAsync(projectId, HttpContext.RequestAborted);

        return Json(new
        {
            ok = result == ApplyPocAnnotationsRevisionResult.Queued,
            message = result switch
            {
                ApplyPocAnnotationsRevisionResult.Queued => "Đã gửi yêu cầu chỉnh sửa — agent đang sửa POC theo các góp ý.",
                ApplyPocAnnotationsRevisionResult.NothingToApply => "Không có góp ý nào chưa xử lý để gom.",
                ApplyPocAnnotationsRevisionResult.NoWaitingRun => "Không có bước nào đang chờ duyệt — chỉ gom được khi workflow đang dừng ở cổng POC.",
                _ => "Đã dùng hết số vòng chỉnh sửa cho bước này."
            }
        });
    }

    // ----- Chia sẻ project: mời thành viên (reviewer/stakeholder) xem & góp ý. -----

    [HttpGet]
    public async Task<IActionResult> Members(Guid projectId)
    {
        var canManageAll = await _permissions.HasPermissionAsync(User, AppPermission.ProjectsViewAll, HttpContext.RequestAborted);
        var result = await _getProjectMembersQuery.ExecuteAsync(projectId, User.Identity?.Name, canManageAll, HttpContext.RequestAborted);
        return result == null ? NotFound() : Json(result);
    }

    [HttpPost]
    public async Task<IActionResult> AddMember(Guid projectId, string? username)
    {
        var canManageAll = await _permissions.HasPermissionAsync(User, AppPermission.ProjectsViewAll, HttpContext.RequestAborted);
        var result = await _addProjectMemberUseCase.ExecuteAsync(projectId, username, User.Identity?.Name, canManageAll, HttpContext.RequestAborted);

        return Json(new
        {
            ok = result == AddProjectMemberResult.Added,
            message = result switch
            {
                AddProjectMemberResult.Added => "Đã thêm thành viên.",
                AddProjectMemberResult.UserNotFound => "Không tìm thấy tài khoản với username này.",
                AddProjectMemberResult.AlreadyMember => "Người này đã là thành viên của project.",
                AddProjectMemberResult.IsOwner => "Người này là chủ project — không cần thêm.",
                AddProjectMemberResult.NotAllowed => "Chỉ chủ project (hoặc TeamDev/Admin) mới được thêm thành viên.",
                _ => "Không tìm thấy project."
            }
        });
    }

    [HttpPost]
    public async Task<IActionResult> RemoveMember(Guid id)
    {
        var canManageAll = await _permissions.HasPermissionAsync(User, AppPermission.ProjectsViewAll, HttpContext.RequestAborted);
        var result = await _removeProjectMemberUseCase.ExecuteAsync(id, User.Identity?.Name, canManageAll, HttpContext.RequestAborted);

        return Json(new
        {
            ok = result == RemoveProjectMemberResult.Removed,
            message = result == RemoveProjectMemberResult.NotAllowed
                ? "Chỉ chủ project (hoặc TeamDev/Admin) mới được gỡ thành viên."
                : null
        });
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
