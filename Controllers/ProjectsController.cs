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
    private readonly CloneProjectUseCase _cloneProjectUseCase;
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
    private readonly SearchAssociatesQuery _searchAssociatesQuery;
    private readonly TriagePocFeedbackUseCase _triagePocFeedbackUseCase;
    private readonly DispatchPocFeedbackUseCase _dispatchPocFeedbackUseCase;
    private readonly AcceptPocUseCase _acceptPocUseCase;
    private readonly IPermissionService _permissions;

    public ProjectsController(
        GetProjectListQuery getProjectListQuery,
        CreateProjectUseCase createProjectUseCase,
        UpdateProjectUseCase updateProjectUseCase,
        CloneProjectUseCase cloneProjectUseCase,
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
        SearchAssociatesQuery searchAssociatesQuery,
        TriagePocFeedbackUseCase triagePocFeedbackUseCase,
        DispatchPocFeedbackUseCase dispatchPocFeedbackUseCase,
        AcceptPocUseCase acceptPocUseCase,
        IPermissionService permissions)
    {
        _getProjectListQuery = getProjectListQuery;
        _createProjectUseCase = createProjectUseCase;
        _updateProjectUseCase = updateProjectUseCase;
        _cloneProjectUseCase = cloneProjectUseCase;
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
        _searchAssociatesQuery = searchAssociatesQuery;
        _triagePocFeedbackUseCase = triagePocFeedbackUseCase;
        _dispatchPocFeedbackUseCase = dispatchPocFeedbackUseCase;
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

    // Nhân bản một dự án để thử nhiều tình huống khác nhau trên cùng một điểm xuất phát. Quyền là
    // ProjectsCreate (kết quả là một project MỚI) chứ không phải ProjectsEdit — dự án gốc không bị đụng
    // tới; [RequireProjectAccess] lo vế còn lại: chỉ nhân bản được project mình có quyền mở.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.ProjectsCreate)]
    [RequireProjectAccess("vm.ProjectId", Message = "Project không tồn tại.")]
    public async Task<IActionResult> Clone(
        CloneProjectVm vm,
        int page = 1,
        int pageSize = GetProjectListQuery.DefaultPageSize,
        string[]? orgUnit = null)
    {
        IActionResult BackToList() => RedirectToAction(nameof(Index), new { page, pageSize, orgUnit });

        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Thông tin nhân bản không hợp lệ. Vui lòng kiểm tra lại.";
            return BackToList();
        }

        var (result, _) = await _cloneProjectUseCase.ExecuteAsync(vm, User.Identity?.Name, HttpContext.RequestAborted);
        switch (result)
        {
            case CloneProjectResult.Cloned:
                TempData["Success"] = vm.Scope == ProjectCloneScope.Full
                    ? "Đã nhân bản dự án (bản sao đầy đủ). Các task đang dở của bản gốc không được chép sang."
                    : "Đã nhân bản dự án (chỉ phần yêu cầu). Bản sao giữ nguyên hội thoại, tài liệu nguồn và các bảng đã chốt.";
                break;
            case CloneProjectResult.NameRequired:
                TempData["Error"] = "Tên bản sao không được để trống.";
                break;
            case CloneProjectResult.WorkspaceCopyFailed:
                TempData["Error"] = "Không chép được thư mục làm việc của dự án nên việc nhân bản đã được hủy (bản sao sẽ không có tài liệu/POC nào). Hãy thử lại sau.";
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

    // Gợi ý người nhận cho ô "Gửi cho ai". Danh bạ nhân sự dùng chung cả công ty, nhưng cửa vào vẫn kẹp
    // theo project + quyền tạo link: chỉ người ĐANG đứng ở một project mình được vào và có quyền chia sẻ
    // mới tra được — không mở thêm một đường tra cứu nhân sự cho mọi tài khoản.
    [HttpGet]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess]
    public async Task<IActionResult> SearchAssociates(Guid projectId, string? q)
    {
        return Json(await _searchAssociatesQuery.ExecuteAsync(q, HttpContext.RequestAborted));
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

    // BƯỚC 1 của lượt gửi ghi chú POC: phân loại từng ghi chú Open thành "chỉnh bản demo" hay "sửa tài
    // liệu yêu cầu" để dựng hộp xác nhận. Chỉ đọc + gọi model, KHÔNG đổi trạng thái gì — người dùng còn
    // đổi nhóm được trước khi bấm gửi ở DispatchPocFeedback.
    //
    // Vì sao trang này không còn bày hai nút: hai đường có chi phí lệch hẳn nhau (một vòng vá HTML có
    // trần, so với soạn lại tài liệu + dựng lại toàn bộ POC), mà việc phân biệt chúng lại là phép phân
    // loại hệ thống tự làm được — không phải việc của người xem demo.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Message = "Project không tồn tại.")]
    public async Task<IActionResult> TriagePocFeedback(Guid projectId)
    {
        var report = await _triagePocFeedbackUseCase.ExecuteAsync(projectId, HttpContext.RequestAborted);

        return report.Status switch
        {
            PocFeedbackTriageStatus.NoOpenComments => Json(new { ok = false, message = "Chưa có ghi chú nào đang chờ gửi — ghim vài ghi chú trên bản demo rồi gửi nhé." }),
            PocFeedbackTriageStatus.ProjectNotFound => NotFound("Project không tồn tại."),
            _ => Json(new
            {
                ok = true,
                classified = report.Classified,
                baConfigured = report.BaConfigured,
                items = report.Items.Select(i => new
                {
                    id = i.Id,
                    index = i.Index,
                    pageView = i.PageView,
                    elementLabel = i.ElementLabel,
                    comment = i.Comment,
                    requirement = i.IsRequirementIssue,
                    reason = i.Reason
                })
            })
        };
    }

    // BƯỚC 2: gửi thật, theo đúng bảng phân loại người dùng vừa xác nhận. Mỗi đường chỉ nhận TẬP CON của
    // nó — trước đây cả hai nút đều nuốt trọn mọi ghi chú Open, nên một buổi review lẫn hai loại thì
    // không nút nào đúng (xem DispatchPocFeedbackUseCase).
    //
    // Cần quyền quản lý requirement: đây là hành động đẩy pipeline/sửa tài liệu, không phải ghim ghi chú.
    // Rào chắn của đường chỉnh demo giữ nguyên — chỉ tác động khi run đang chờ ở ĐÚNG bước POC, không
    // duyệt/không đẩy bước kế, vẫn đếm chung trần DeliveryPipeline.MaxRevisionRounds.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Message = "Project không tồn tại.")]
    public async Task<IActionResult> DispatchPocFeedback(Guid projectId, Guid[]? fixIds, Guid[]? requirementIds)
    {
        var report = await _dispatchPocFeedbackUseCase.ExecuteAsync(
            projectId,
            fixIds ?? Array.Empty<Guid>(),
            requirementIds ?? Array.Empty<Guid>(),
            HttpContext.RequestAborted);

        return report.Status switch
        {
            PocFeedbackDispatchStatus.Ok => Json(new { ok = true, message = DispatchOkMessage(report) }),
            PocFeedbackDispatchStatus.NothingSelected => Json(new { ok = false, message = "Chưa chọn ghi chú nào để gửi." }),
            PocFeedbackDispatchStatus.InvalidSelection => Json(new { ok = false, reload = true, message = "Danh sách ghi chú vừa thay đổi (có người khác gửi hoặc xóa) — mở lại để phân loại theo bản mới nhé." }),
            PocFeedbackDispatchStatus.RequirementFailed => Json(new { ok = false, message = RequirementErrorMessage(report.RequirementError) }),
            PocFeedbackDispatchStatus.FixFailed => Json(new { ok = false, message = FixErrorMessage(report.FixError) }),
            _ => NotFound("Project không tồn tại.")
        };
    }

    // Đường tài liệu ĐÈ đường chỉnh demo trong cùng một lượt: POC sẽ được dựng lại từ tài liệu đã sửa nên
    // vá HTML lúc này vừa phí một vòng trong trần, vừa cho ra bản vá bị bỏ đi ngay. Các ghi chú thẩm mỹ
    // được giữ Open cho vòng review sau — phải nói rõ điều đó, nếu không người dùng tưởng chúng đã đi.
    private static string DispatchOkMessage(PocFeedbackDispatchReport report)
    {
        if (report.RoutedCount == 0)
            return $"Đã gửi {report.FixSentCount} ghi chú cho đội Dev chỉnh bản demo — theo dõi tiến độ ở trang Requirements, xong sẽ có bản mới để xem lại.";

        var message = $"Đã gửi {report.RoutedCount} điểm hiểu sai yêu cầu về BA để cập nhật tài liệu — hệ thống đang soạn lại bản mô tả, sau đó anh/chị duyệt lại để dựng bản demo mới.";

        if (report.HeldCount > 0)
            message += $" {report.HeldCount} ghi chú chỉnh trình bày được GIỮ LẠI (vẫn ở trạng thái chờ gửi): bản demo sắp dựng lại từ đầu nên chưa cần tốn một vòng chỉnh sửa, anh/chị xem lại chúng ở vòng review tới.";

        return message;
    }

    private static string RequirementErrorMessage(RoutePocFeedbackResult? error) => error switch
    {
        RoutePocFeedbackResult.BaNotConfigured => "Chưa cấu hình agent BA (RoleKey = BusinessAnalyst) nên chưa gửi về Requirement được.",
        RoutePocFeedbackResult.ComposeFailed => "Chưa soạn được phản hồi gửi BA (lỗi gọi model) — ghi chú vẫn còn nguyên, thử lại giúp nhé.",
        RoutePocFeedbackResult.NoOpenComments => "Các ghi chú đã chọn không còn ở trạng thái chờ gửi — mở lại để phân loại theo bản mới nhé.",
        _ => "Không gửi được về Requirement — ghi chú vẫn còn nguyên, thử lại giúp nhé."
    };

    private static string FixErrorMessage(RequestStageRevisionResult? error) => error switch
    {
        RequestStageRevisionResult.RevisionLimitReached => $"Bản demo đã qua {DeliveryPipeline.MaxRevisionRounds} vòng chỉnh sửa. Nếu vẫn chưa đúng thì thường là do TÀI LIỆU chưa khớp — hãy xếp các ghi chú đó sang nhóm sửa tài liệu yêu cầu.",
        RequestStageRevisionResult.StageMismatch => "Quy trình đã đi qua bước bản demo nên không chỉnh ở đây được nữa — nhờ đội Dev xử lý trên Agent Dashboard.",
        RequestStageRevisionResult.MissingFeedback => "Các ghi chú đã chọn không còn ở trạng thái chờ gửi — mở lại để phân loại theo bản mới nhé.",
        _ => "Không có bản demo nào đang chờ duyệt để chỉnh sửa."
    };

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
