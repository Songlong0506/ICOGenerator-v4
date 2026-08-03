using ICOGenerator.Application.Projects;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Security;
using ICOGenerator.Services.Workflows;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// Xem dự án là quyền cơ bản nhất; mọi action GET trong controller đều yêu cầu ProjectsView.
[RequirePermission(AppPermission.ProjectsView)]
public class ProjectsController : Controller
{
    private readonly GetProjectListQuery _getProjectListQuery;
    private readonly CreateProjectUseCase _createProjectUseCase;
    private readonly UpdateProjectUseCase _updateProjectUseCase;
    private readonly GetMockupFileQuery _getMockupFileQuery;
    private readonly GetImplementationSourceQuery _getImplementationSourceQuery;
    private readonly GetPocReviewQuery _getPocReviewQuery;
    private readonly ListPocCommentsQuery _listPocCommentsQuery;
    private readonly AddPocCommentUseCase _addPocCommentUseCase;
    private readonly DeletePocCommentUseCase _deletePocCommentUseCase;
    private readonly ReopenPocCommentUseCase _reopenPocCommentUseCase;
    private readonly CreatePocShareLinkUseCase _createPocShareLinkUseCase;
    private readonly RevokePocShareLinkUseCase _revokePocShareLinkUseCase;
    private readonly ListPocShareLinksQuery _listPocShareLinksQuery;
    private readonly RoutePocFeedbackToRequirementUseCase _routePocFeedbackUseCase;
    private readonly RequestStageRevisionUseCase _requestStageRevisionUseCase;
    private readonly AcceptPocUseCase _acceptPocUseCase;
    private readonly IPermissionService _permissions;

    public ProjectsController(
        GetProjectListQuery getProjectListQuery,
        CreateProjectUseCase createProjectUseCase,
        UpdateProjectUseCase updateProjectUseCase,
        GetMockupFileQuery getMockupFileQuery,
        GetImplementationSourceQuery getImplementationSourceQuery,
        GetPocReviewQuery getPocReviewQuery,
        ListPocCommentsQuery listPocCommentsQuery,
        AddPocCommentUseCase addPocCommentUseCase,
        DeletePocCommentUseCase deletePocCommentUseCase,
        ReopenPocCommentUseCase reopenPocCommentUseCase,
        CreatePocShareLinkUseCase createPocShareLinkUseCase,
        RevokePocShareLinkUseCase revokePocShareLinkUseCase,
        ListPocShareLinksQuery listPocShareLinksQuery,
        RoutePocFeedbackToRequirementUseCase routePocFeedbackUseCase,
        RequestStageRevisionUseCase requestStageRevisionUseCase,
        AcceptPocUseCase acceptPocUseCase,
        IPermissionService permissions)
    {
        _getProjectListQuery = getProjectListQuery;
        _createProjectUseCase = createProjectUseCase;
        _updateProjectUseCase = updateProjectUseCase;
        _getMockupFileQuery = getMockupFileQuery;
        _getImplementationSourceQuery = getImplementationSourceQuery;
        _getPocReviewQuery = getPocReviewQuery;
        _listPocCommentsQuery = listPocCommentsQuery;
        _addPocCommentUseCase = addPocCommentUseCase;
        _deletePocCommentUseCase = deletePocCommentUseCase;
        _reopenPocCommentUseCase = reopenPocCommentUseCase;
        _createPocShareLinkUseCase = createPocShareLinkUseCase;
        _revokePocShareLinkUseCase = revokePocShareLinkUseCase;
        _listPocShareLinksQuery = listPocShareLinksQuery;
        _routePocFeedbackUseCase = routePocFeedbackUseCase;
        _requestStageRevisionUseCase = requestStageRevisionUseCase;
        _acceptPocUseCase = acceptPocUseCase;
        _permissions = permissions;
    }

    // Các action theo projectId (Mockup/PocReview/DownloadSource...) chặn truy cập chéo bằng
    // [RequireProjectAccess]: user thường chỉ đụng được project mình tạo (xem IProjectAccessGuard).
    // Trả về như "không tồn tại" để không xác nhận sự tồn tại của project với người ngoài.

    public async Task<IActionResult> Index(
        int page = 1,
        int pageSize = GetProjectListQuery.DefaultPageSize,
        string[]? orgUnit = null)
    {
        // Admin/TeamDev (quyền ProjectsViewAll) thấy mọi project; User thường chỉ thấy project mình tạo.
        var canViewAll = await _permissions.HasPermissionAsync(User, AppPermission.ProjectsViewAll, HttpContext.RequestAborted);
        var result = await _getProjectListQuery.ExecuteAsync(page, pageSize, User.Identity?.Name, canViewAll, orgUnit);
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

    // Sửa Name/Description/đơn vị yêu cầu của một project. Quyền ProjectsEdit là mức HÀNH ĐỘNG; ngoài ra
    // vẫn qua IProjectAccessGuard nên User thường chỉ sửa được project của chính mình (Admin/TeamDev có
    // ProjectsViewAll sửa được tất cả) — cùng luật với các action theo projectId khác trong controller.
    // Trả về đúng trang/bộ lọc người dùng đang xem để họ không bị "nhảy" về trang 1 sau khi lưu.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.ProjectsEdit)]
    [RequireProjectAccess("vm.ProjectId", Message = "Project không tồn tại.")]
    public async Task<IActionResult> Update(
        ProjectEditVm vm,
        int page = 1,
        int pageSize = GetProjectListQuery.DefaultPageSize,
        string[]? orgUnit = null)
    {
        IActionResult BackToList() => RedirectToAction(nameof(Index), new { page, pageSize, orgUnit });

        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Thông tin dự án không hợp lệ. Vui lòng kiểm tra lại.";
            return BackToList();
        }

        var result = await _updateProjectUseCase.ExecuteAsync(vm, HttpContext.RequestAborted);
        switch (result)
        {
            case UpdateProjectResult.Updated:
                TempData["Success"] = "Đã cập nhật thông tin dự án.";
                break;
            case UpdateProjectResult.NoChange:
                TempData["Info"] = "Không có thay đổi nào để lưu.";
                break;
            case UpdateProjectResult.NameRequired:
                TempData["Error"] = "Tên dự án không được để trống.";
                break;
            case UpdateProjectResult.RenameBlockedByRunningWorkflow:
                TempData["Warning"] = "Dự án đang chạy workflow nên chưa đổi được tên (tên dự án cũng là tên thư mục làm việc của agent). Hãy đợi workflow kết thúc — mô tả và đơn vị yêu cầu vẫn sửa được bình thường.";
                break;
            case UpdateProjectResult.WorkspaceRenameFailed:
                TempData["Error"] = "Không đổi được tên thư mục làm việc của dự án nên thay đổi đã được hủy để không bỏ rơi tài liệu/POC đã sinh. Hãy thử lại sau (có thể một file trong thư mục đang mở).";
                break;
            default:
                TempData["Error"] = "Project không tồn tại.";
                break;
        }

        return BackToList();
    }

    // version (tùy chọn): mở lại BẢN CHỤP của một vòng dựng trước thay vì bản hiện tại — xem PocSnapshots.
    // Chỉ đọc, cùng quyền và cùng rào sandbox với bản hiện tại; số vòng được tra trong danh sách file có
    // thật nên không ghép được đường dẫn tùy ý.
    [RequireProjectAccess(Message = "Mockup file not found.")]
    public async Task<IActionResult> Mockup(Guid projectId, bool review = false, int? version = null)
    {
        var result = await _getMockupFileQuery.ExecuteAsync(projectId, version);
        if (result == null)
            return NotFound("Mockup file not found.");

        // poc-demo.html leads with a big developer-agent instruction comment copied from poc-template.html.
        // It is guidance for the LLM, not page content, and a disturbed copy of it renders as raw
        // "(POC_SCRIPT_START/END) holds ONE …" text instead of the POC (the "Mockup button opens a broken
        // page" bug). Strip it before serving so the browser always gets the shell + content, including for
        // demos generated before this fix. The file is small self-contained HTML, so reading it into memory
        // (rather than streaming the physical file) is fine.
        var html = await System.IO.File.ReadAllTextAsync(result.FilePath, HttpContext.RequestAborted);
        html = PocTemplate.StripDeveloperGuide(html);

        // REVIEW mode (nhúng trong iframe của trang PocReview): tiêm annotator để người xem ghim ghi chú
        // lên phần tử. Annotator chỉ nói chuyện với trang cha qua postMessage — sandbox bên dưới giữ nguyên
        // (origin opaque, không cookie), nên review mode KHÔNG nới rào chắn bảo mật nào.
        if (review)
            html = PocTemplate.InjectAnnotator(html);

        // This HTML is agent/LLM-generated and served from our own origin. Sandbox it so any injected
        // <script> runs in an opaque origin — no access to the admin auth cookie and no authenticated
        // same-origin POSTs (e.g. to Settings) — closing the prompt-injection escalation path.
        // 'allow-scripts' keeps the demo interactive; 'allow-forms'/'allow-modals' let the POC CRUD
        // submit forms and use confirm()/alert() dialogs. 'allow-same-origin' is deliberately omitted —
        // that omission (opaque origin, no auth cookie, no authenticated same-origin POSTs) is the
        // actual security boundary, and forms/modals don't weaken it.
        Response.Headers["Content-Security-Policy"] = "sandbox allow-scripts allow-forms allow-modals;";
        return Content(html, "text/html; charset=utf-8");
    }

    // ==== Review POC: xem POC trong iframe + ghim ghi chú trực tiếp lên phần tử ====
    // Cùng quyền ProjectsView với Mockup (xem = review); GHI ghi chú cũng chỉ cần ProjectsView — cùng
    // triết lý với Feedback (quyền View đủ để GỬI phản hồi của chính mình), vì đây chính là kênh phản
    // hồi của người dùng cuối về POC. Xóa bị siết ở use case: chủ ghi chú hoặc người có DeliveryAdvance.

    [RequireProjectAccess(Denial = ProjectAccessDenial.RedirectToProjects)]
    public async Task<IActionResult> PocReview(Guid projectId)
    {
        var result = await _getPocReviewQuery.ExecuteAsync(projectId, HttpContext.RequestAborted);
        if (result == null)
            return RedirectToAction(nameof(Index));

        if (!result.HasMockup)
        {
            TempData["Error"] = "Project này chưa có POC demo để review.";
            return RedirectToAction(nameof(Index));
        }

        ViewBag.CanManageComments = await _permissions.HasPermissionAsync(
            User, AppPermission.DeliveryAdvance, HttpContext.RequestAborted);
        // Hai hành động "đóng vòng" của trang này (gửi về Requirement, nhờ Dev chỉnh bản demo) đều yêu cầu
        // RequirementsManage ở endpoint — nên UI phải soi ĐÚNG quyền đó. Trước đây chúng bị treo sau
        // CanManageComments (DeliveryAdvance), tức là ẩn khỏi chính người dùng nghiệp vụ được phép bấm.
        ViewBag.CanManageRequirements = await _permissions.HasPermissionAsync(
            User, AppPermission.RequirementsManage, HttpContext.RequestAborted);
        return View(result);
    }

    [HttpGet]
    [RequireProjectAccess]
    public async Task<IActionResult> PocComments(Guid projectId)
    {
        var canManage = await _permissions.HasPermissionAsync(
            User, AppPermission.DeliveryAdvance, HttpContext.RequestAborted);
        return Json(await _listPocCommentsQuery.ExecuteAsync(
            projectId, User.Identity?.Name, canManage, HttpContext.RequestAborted));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequireProjectAccess(Message = "Project không tồn tại.")]
    public async Task<IActionResult> AddPocComment(
        Guid projectId, string? pageView, string? elementLabel, string? elementPath,
        double xPercent, double yPercent, string? comment)
    {
        var (result, item) = await _addPocCommentUseCase.ExecuteAsync(
            projectId, pageView, elementLabel, elementPath, xPercent, yPercent, comment,
            User.Identity?.Name, HttpContext.RequestAborted);

        return result switch
        {
            AddPocCommentResult.Ok => Json(item),
            AddPocCommentResult.MissingComment => BadRequest("Nội dung ghi chú trống."),
            AddPocCommentResult.TooManyComments => BadRequest("Project đã có quá nhiều ghi chú — hãy xóa bớt trước khi ghim thêm."),
            _ => NotFound("Project không tồn tại.")
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePocComment(Guid id)
    {
        var canManage = await _permissions.HasPermissionAsync(
            User, AppPermission.DeliveryAdvance, HttpContext.RequestAborted);
        var deleted = await _deletePocCommentUseCase.ExecuteAsync(
            id, User.Identity?.Name, canManage, HttpContext.RequestAborted);

        return deleted ? Json(new { ok = true }) : NotFound();
    }

    // "Vẫn chưa đạt": mở lại một ghi chú mà vòng sửa đã tuyên bố xử lý xong, để nó vào yêu cầu chỉnh sửa
    // TIẾP THEO. Không có đường này thì muốn nhắc lại cùng một lỗi phải ghim ghi chú mới, và danh sách
    // phình lên bằng các bản sao của cùng một vấn đề.
    [HttpPost]
    [ValidateAntiForgeryToken]
    // Kiểm quyền TRƯỚC khi đụng dữ liệu; use case chỉ nhận ghi chú thuộc đúng project đã kiểm.
    [RequireProjectAccess]
    public async Task<IActionResult> ReopenPocComment(Guid projectId, Guid id)
    {
        var result = await _reopenPocCommentUseCase.ExecuteAsync(projectId, id, HttpContext.RequestAborted);
        return result switch
        {
            ReopenPocCommentResult.Ok => Json(new { ok = true }),
            ReopenPocCommentResult.NotFound => NotFound(),
            _ => BadRequest("Ghi chú này chưa qua vòng chỉnh sửa nào (hoặc đã được gửi ngược về bước Requirement).")
        };
    }

    // ==== Link chia sẻ bản demo cho người KHÔNG có tài khoản ====
    // Nghiệm thu thật luôn có nhiều người, nhưng trang review bị chặn theo quyền truy cập project. Ba
    // action dưới đây cấp/thu hồi/liệt kê "chìa khoá dạng ai-có-link-nấy-vào-được" — luôn có hạn dùng và
    // thu hồi được. Bề mặt mà khách chạm tới nằm ở PocShareController.

    [HttpGet]
    [RequireProjectAccess]
    public async Task<IActionResult> PocShareLinks(Guid projectId)
    {
        return Json(await _listPocShareLinksQuery.ExecuteAsync(projectId, HttpContext.RequestAborted));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess]
    public async Task<IActionResult> CreatePocShareLink(Guid projectId, string? label, int days = CreatePocShareLinkUseCase.DefaultDays)
    {
        var link = await _createPocShareLinkUseCase.ExecuteAsync(
            projectId, label, days, User.Identity?.Name, HttpContext.RequestAborted);
        if (link == null)
            return BadRequest("Dự án đã có quá nhiều link chia sẻ còn hiệu lực — thu hồi bớt trước khi tạo link mới.");

        // URL tuyệt đối để người tạo copy dán thẳng vào chat/email.
        var url = Url.Action("Open", "PocShare", new { token = link.Token }, Request.Scheme);
        return Json(new { link.Id, link.Token, link.Label, link.ExpiresAtUtc, url });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess]
    public async Task<IActionResult> RevokePocShareLink(Guid projectId, Guid id)
    {
        return await _revokePocShareLinkUseCase.ExecuteAsync(projectId, id, HttpContext.RequestAborted)
            ? Json(new { ok = true })
            : NotFound();
    }

    // Đóng vòng POC → TÀI LIỆU: lọc các ghi chú Open phản ánh hiểu-sai-yêu-cầu, đưa vào hội thoại BA và
    // soạn lại draft. Cần quyền quản lý requirement (đây là hành động sửa tài liệu, không phải ghim ghi chú).
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Message = "Project không tồn tại.")]
    public async Task<IActionResult> RoutePocFeedbackToRequirement(Guid projectId)
    {
        var result = await _routePocFeedbackUseCase.ExecuteAsync(projectId, HttpContext.RequestAborted);
        return result switch
        {
            RoutePocFeedbackResult.Ok => Json(new { ok = true, message = "Đã gửi các điểm thuộc yêu cầu về BA để cập nhật tài liệu — hệ thống đang soạn lại bản mô tả, sau đó anh/chị duyệt lại để dựng POC mới." }),
            RoutePocFeedbackResult.NoOpenComments => Json(new { ok = false, message = "Chưa có ghi chú nào đang mở để gửi." }),
            RoutePocFeedbackResult.NoRequirementIssue => Json(new { ok = false, message = "Các ghi chú hiện tại chỉ là chỉnh trình bày (không phải hiểu sai yêu cầu) — hãy dùng \"Yêu cầu chỉnh sửa\" ở cổng POC để đội Dev sửa demo." }),
            RoutePocFeedbackResult.BaNotConfigured => Json(new { ok = false, message = "Chưa cấu hình agent BA (RoleKey = BusinessAnalyst)." }),
            _ => NotFound("Project không tồn tại.")
        };
    }

    // Đường "nhờ đội Dev chỉnh BẢN DEMO" cho user thường, ngay tại trang POC Review.
    //
    // Vì sao cần riêng: cổng duyệt delivery (ApproveStage/RequestRevision ở Agent Dashboard) đòi quyền
    // DeliveryAdvance mà user nghiệp vụ không có, còn nút "gửi về Requirement" cạnh đây thì CỐ TÌNH bỏ
    // qua các ghi chú thuần trình bày (xem poc-feedback-route.v1.md). Hệ quả cũ: đúng loại lỗi mà người
    // xem demo hay bắt nhất — nhãn sai, thiếu nút, bảng trống, canh lệch — lại không có đường nào để họ
    // xử lý, phải đi nhờ TeamDev.
    //
    // Rào chắn giữ nguyên: chỉ tác động khi run đang chờ ở ĐÚNG bước POC (onlyStage), không duyệt/không
    // đẩy bước kế, và vẫn đếm chung trần DeliveryPipeline.MaxRevisionRounds như đường của người duyệt.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Message = "Project không tồn tại.")]
    public async Task<IActionResult> RequestPocFix(Guid projectId, string? feedback)
    {
        var result = await _requestStageRevisionUseCase.ExecuteAsync(
            projectId, feedback, runId: null, includePocComments: true, onlyStage: WorkflowStageKey.PocPreview);

        return result switch
        {
            RequestStageRevisionResult.Queued => Json(new { ok = true, message = "Đã gửi cho đội Dev chỉnh bản demo — theo dõi tiến độ ở trang Requirements, xong sẽ có bản mới để xem lại." }),
            RequestStageRevisionResult.MissingFeedback => Json(new { ok = false, message = "Chưa có ghi chú nào đang mở và cũng chưa gõ nhận xét — ghim vài ghi chú trên POC rồi gửi nhé." }),
            RequestStageRevisionResult.RevisionLimitReached => Json(new { ok = false, message = $"Bản demo đã qua {DeliveryPipeline.MaxRevisionRounds} vòng chỉnh sửa. Nếu vẫn chưa đúng thì thường là do TÀI LIỆU chưa khớp — hãy dùng nút gửi về Requirement." }),
            RequestStageRevisionResult.StageMismatch => Json(new { ok = false, message = "Quy trình đã đi qua bước bản demo nên không chỉnh ở đây được nữa — nhờ đội Dev xử lý trên Agent Dashboard." }),
            _ => Json(new { ok = false, message = "Không có bản demo nào đang chờ duyệt để chỉnh sửa." })
        };
    }

    // NGHIỆM THU BẢN DEMO: điểm dừng của hành trình phía người yêu cầu. Trước đây họ chỉ có các đường
    // "còn sai chỗ này" (ghim ghi chú / nhờ Dev chỉnh / gửi về Requirement) mà không có đường nào nói
    // "được rồi", nên đội delivery phải đi hỏi miệng và chặng cuối stepper không bao giờ đóng.
    // KHÔNG đẩy pipeline: chỉ ghi lại ai/lúc nào + báo cho người có quyền duyệt (xem AcceptPocUseCase).
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Message = "Project không tồn tại.")]
    public async Task<IActionResult> AcceptPoc(Guid projectId)
    {
        var result = await _acceptPocUseCase.ExecuteAsync(
            projectId, User.Identity?.Name ?? string.Empty, HttpContext.RequestAborted);

        return result switch
        {
            AcceptPocResult.Ok => Json(new { ok = true, message = "Đã ghi nhận anh/chị nghiệm thu bản demo — đội delivery đã được báo để đi tiếp các bước sau." }),
            AcceptPocResult.AlreadyAccepted => Json(new { ok = false, message = "Bản demo này đã được nghiệm thu trước đó rồi." }),
            AcceptPocResult.NoPoc => Json(new { ok = false, message = "Chưa có bản demo nào để nghiệm thu." }),
            _ => NotFound("Project không tồn tại.")
        };
    }

    // Packages the agent-generated multi-file app (04_Implementation/src) into a .zip the user can
    // download — the only way to actually get the produced source out of the workspace.
    [RequireProjectAccess(Message = "Chưa có source code để tải. Hãy chạy tới bước Implementation để agent sinh code trong 04_Implementation/src.")]
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
