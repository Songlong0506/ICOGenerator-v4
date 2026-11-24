using ICOGenerator.Localization;
using ICOGenerator.Application.Projects;
using ICOGenerator.Application.Requirements;
using ICOGenerator.Domain.Enums;
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
    private readonly WithdrawPocCommentUseCase _withdrawPocCommentUseCase;
    private readonly ReopenPocCommentUseCase _reopenPocCommentUseCase;
    private readonly CreatePocShareLinkUseCase _createPocShareLinkUseCase;
    private readonly RevokePocShareLinkUseCase _revokePocShareLinkUseCase;
    private readonly ListPocShareLinksQuery _listPocShareLinksQuery;
    private readonly SearchAssociatesQuery _searchAssociatesQuery;
    private readonly TriagePocFeedbackUseCase _triagePocFeedbackUseCase;
    private readonly DispatchPocFeedbackUseCase _dispatchPocFeedbackUseCase;
    private readonly AcceptPocUseCase _acceptPocUseCase;
    private readonly WithdrawPocAcceptanceUseCase _withdrawPocAcceptanceUseCase;
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
        WithdrawPocCommentUseCase withdrawPocCommentUseCase,
        ReopenPocCommentUseCase reopenPocCommentUseCase,
        CreatePocShareLinkUseCase createPocShareLinkUseCase,
        RevokePocShareLinkUseCase revokePocShareLinkUseCase,
        ListPocShareLinksQuery listPocShareLinksQuery,
        SearchAssociatesQuery searchAssociatesQuery,
        TriagePocFeedbackUseCase triagePocFeedbackUseCase,
        DispatchPocFeedbackUseCase dispatchPocFeedbackUseCase,
        AcceptPocUseCase acceptPocUseCase,
        WithdrawPocAcceptanceUseCase withdrawPocAcceptanceUseCase,
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
        _withdrawPocCommentUseCase = withdrawPocCommentUseCase;
        _reopenPocCommentUseCase = reopenPocCommentUseCase;
        _createPocShareLinkUseCase = createPocShareLinkUseCase;
        _revokePocShareLinkUseCase = revokePocShareLinkUseCase;
        _listPocShareLinksQuery = listPocShareLinksQuery;
        _searchAssociatesQuery = searchAssociatesQuery;
        _triagePocFeedbackUseCase = triagePocFeedbackUseCase;
        _dispatchPocFeedbackUseCase = dispatchPocFeedbackUseCase;
        _acceptPocUseCase = acceptPocUseCase;
        _withdrawPocAcceptanceUseCase = withdrawPocAcceptanceUseCase;
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
        // Form đã đánh dấu required cả Name lẫn Org Unit, nhưng validate lại ở server: HTML required chỉ là
        // lớp tiện dụng, request tự chế bỏ qua nó dễ dàng. Báo lý do bằng toast thay vì im lặng quay về
        // danh sách — người dùng bấm "Create Project" mà không thấy gì xảy ra là bug khó hiểu nhất.
        if (!ModelState.IsValid)
        {
            TempData["Error"] = Loc.T("ProjectsController.Create");
            return RedirectToAction(nameof(Index));
        }

        var outcome = await _createProjectUseCase.ExecuteAsync(vm, User.Identity?.Name);
        switch (outcome.Result)
        {
            case CreateProjectResult.Created:
                TempData["Success"] = Loc.T("ProjectsController.Create.2");
                break;
            case CreateProjectResult.NameRequired:
                TempData["Error"] = Loc.T("ProjectsController.Create.3");
                break;
            default:
                TempData["Error"] = Loc.T("ProjectsController.Create.4");
                break;
        }

        return RedirectToAction(nameof(Index));
    }

    // Sửa Name/Description/đơn vị yêu cầu của một project. Quyền ProjectsEdit là mức HÀNH ĐỘNG; ngoài ra
    // vẫn qua IProjectAccessGuard nên User thường chỉ sửa được project của chính mình (Admin/TeamDev có
    // ProjectsViewAll sửa được tất cả) — cùng luật với các action theo projectId khác trong controller.
    // Trả về đúng trang/bộ lọc người dùng đang xem để họ không bị "nhảy" về trang 1 sau khi lưu.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.ProjectsEdit)]
    [RequireProjectAccess("vm.ProjectId", Message = "Project.NotFound")]
    public async Task<IActionResult> Update(
        ProjectEditVm vm,
        int page = 1,
        int pageSize = GetProjectListQuery.DefaultPageSize,
        string[]? orgUnit = null)
    {
        IActionResult BackToList() => RedirectToAction(nameof(Index), new { page, pageSize, orgUnit });

        if (!ModelState.IsValid)
        {
            TempData["Error"] = Loc.T("ProjectsController.Update");
            return BackToList();
        }

        var result = await _updateProjectUseCase.ExecuteAsync(vm, HttpContext.RequestAborted);
        switch (result)
        {
            case UpdateProjectResult.Updated:
                TempData["Success"] = Loc.T("ProjectsController.Update.2");
                break;
            case UpdateProjectResult.NoChange:
                TempData["Info"] = Loc.T("ProjectsController.Update.3");
                break;
            case UpdateProjectResult.NameRequired:
                TempData["Error"] = Loc.T("ProjectsController.Update.4");
                break;
            case UpdateProjectResult.RenameBlockedByRunningWorkflow:
                TempData["Warning"] = Loc.T("ProjectsController.Update.5");
                break;
            case UpdateProjectResult.WorkspaceRenameFailed:
                TempData["Error"] = Loc.T("ProjectsController.Update.6");
                break;
            default:
                TempData["Error"] = Loc.T("ProjectsController.Update.7");
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
    [RequireProjectAccess("vm.ProjectId", Message = "Project.NotFound")]
    public async Task<IActionResult> Clone(
        CloneProjectVm vm,
        int page = 1,
        int pageSize = GetProjectListQuery.DefaultPageSize,
        string[]? orgUnit = null)
    {
        IActionResult BackToList() => RedirectToAction(nameof(Index), new { page, pageSize, orgUnit });

        if (!ModelState.IsValid)
        {
            TempData["Error"] = Loc.T("ProjectsController.Clone");
            return BackToList();
        }

        var (result, _) = await _cloneProjectUseCase.ExecuteAsync(vm, User.Identity?.Name, HttpContext.RequestAborted);
        switch (result)
        {
            case CloneProjectResult.Cloned:
                TempData["Success"] = vm.Scope == ProjectCloneScope.Full
                    ? Loc.T("ProjectsController.Clone.2")
                    : Loc.T("ProjectsController.Clone.3");
                break;
            case CloneProjectResult.NameRequired:
                TempData["Error"] = Loc.T("ProjectsController.Clone.4");
                break;
            case CloneProjectResult.WorkspaceCopyFailed:
                TempData["Error"] = Loc.T("ProjectsController.Clone.5");
                break;
            default:
                TempData["Error"] = Loc.T("ProjectsController.Clone.6");
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

        return await PocDemoResponse.BuildAsync(this, result.FilePath, review, HttpContext.RequestAborted);
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
            TempData["Error"] = Loc.T("ProjectsController.PocReview");
            return RedirectToAction(nameof(Index));
        }

        ViewBag.CanManageComments = await _permissions.HasPermissionAsync(
            User, AppPermission.DeliveryAdvance, HttpContext.RequestAborted);
        // Hai hành động "đóng vòng" của trang này (gửi về Requirement, nhờ Dev chỉnh bản demo) đều yêu cầu
        // RequirementsManage ở endpoint — nên UI phải soi ĐÚNG quyền đó. Trước đây chúng bị treo sau
        // CanManageComments (DeliveryAdvance), tức là ẩn khỏi chính người dùng nghiệp vụ được phép bấm.
        ViewBag.CanManageRequirements = await _permissions.HasPermissionAsync(
            User, AppPermission.RequirementsManage, HttpContext.RequestAborted);
        // "Thử model khác" (biến thể POC, PocVariantsController) — nút chỉ hiện với đúng quyền endpoint đòi.
        ViewBag.CanExperimentModels = await _permissions.HasPermissionAsync(
            User, AppPermission.PocModelExperiment, HttpContext.RequestAborted);
        return View(result);
    }

    [HttpGet]
    [RequireProjectAccess]
    public async Task<IActionResult> PocComments(Guid projectId)
    {
        var canManage = await _permissions.HasPermissionAsync(
            User, AppPermission.DeliveryAdvance, HttpContext.RequestAborted);
        return Json(await _listPocCommentsQuery.ExecuteAsync(
            projectId, User.Identity?.Name, canManage, includeBriefNotes: true, HttpContext.RequestAborted));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequireProjectAccess(Message = "Project.NotFound")]
    public async Task<IActionResult> AddPocComment(
        Guid projectId, string? pageView, string? elementLabel, string? elementPath,
        double xPercent, double yPercent, string? comment)
    {
        var (result, item) = await _addPocCommentUseCase.ExecuteAsync(
            projectId, pageView, elementLabel, elementPath, xPercent, yPercent, comment,
            User.Identity?.Name, cancellationToken: HttpContext.RequestAborted);

        return result switch
        {
            AddPocCommentResult.Ok => Json(item),
            AddPocCommentResult.MissingComment => BadRequest(Loc.T("ProjectsController.AddPocComment")),
            AddPocCommentResult.TooManyComments => BadRequest(Loc.T("ProjectsController.AddPocComment.2")),
            AddPocCommentResult.PocAccepted => Conflict(PocAcceptanceGate.LockedMessage),
            _ => NotFound(Loc.T("ProjectsController.AddPocComment.3"))
        };
    }

    // Thu hồi: ghi chú CHƯA GỬI ĐI bị xoá thật (không để lại dòng lịch sử nào — chưa ai ngoài người gõ
    // nhìn thấy nó); ghi chú đã từng gửi đi rồi được mở lại chỉ đóng dấu "đã thu hồi bởi X" và ở lại bảng
    // lịch sử. Xem WithdrawPocCommentUseCase.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WithdrawPocComment(Guid id)
    {
        var canManage = await _permissions.HasPermissionAsync(
            User, AppPermission.DeliveryAdvance, HttpContext.RequestAborted);
        var result = await _withdrawPocCommentUseCase.ExecuteAsync(
            id, User.Identity?.Name, canManage, HttpContext.RequestAborted);

        return result switch
        {
            WithdrawPocCommentResult.Ok => Json(new { ok = true }),
            WithdrawPocCommentResult.AlreadyDispatched =>
                BadRequest(Loc.T("ProjectsController.WithdrawPocComment")),
            WithdrawPocCommentResult.PocAccepted => Conflict(PocAcceptanceGate.LockedMessage),
            _ => NotFound()
        };
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
            ReopenPocCommentResult.PocAccepted => Conflict(PocAcceptanceGate.LockedMessage),
            _ => BadRequest(Loc.T("ProjectsController.ReopenPocComment"))
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
        var (result, link) = await _createPocShareLinkUseCase.ExecuteAsync(
            projectId, label, days, User.Identity?.Name, HttpContext.RequestAborted);
        switch (result)
        {
            case CreatePocShareLinkResult.MissingRecipient:
                return BadRequest(Loc.T("ProjectsController.CreatePocShareLink.2"));
            case CreatePocShareLinkResult.TooManyLinks:
                return BadRequest(Loc.T("ProjectsController.CreatePocShareLink"));
            case not CreatePocShareLinkResult.Ok:
                return NotFound();
        }

        // URL tuyệt đối để người tạo copy dán thẳng vào chat/email.
        var url = Url.Action("Open", "PocShare", new { token = link.Token }, Request.Scheme);
        return Json(new { link!.Id, link.Token, link.Label, link.ExpiresAtUtc, url });
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
    [RequireProjectAccess(Message = "Project.NotFound")]
    public async Task<IActionResult> TriagePocFeedback(Guid projectId)
    {
        var report = await _triagePocFeedbackUseCase.ExecuteAsync(projectId, HttpContext.RequestAborted);

        return report.Status switch
        {
            PocFeedbackTriageStatus.NoOpenComments => Json(new { ok = false, message = Loc.T("ProjectsController.TriagePocFeedback") }),
            PocFeedbackTriageStatus.ProjectNotFound => NotFound(Loc.T("ProjectsController.TriagePocFeedback.2")),
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
                    design = i.IsDesignIssue,
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
    [RequireProjectAccess(Message = "Project.NotFound")]
    public async Task<IActionResult> DispatchPocFeedback(Guid projectId, Guid[]? fixIds, Guid[]? requirementIds, Guid[]? designIds)
    {
        var report = await _dispatchPocFeedbackUseCase.ExecuteAsync(
            projectId,
            fixIds ?? Array.Empty<Guid>(),
            requirementIds ?? Array.Empty<Guid>(),
            designIds ?? Array.Empty<Guid>(),
            HttpContext.RequestAborted);

        return report.Status switch
        {
            PocFeedbackDispatchStatus.Ok => Json(new { ok = true, message = DispatchOkMessage(report) }),
            PocFeedbackDispatchStatus.NothingSelected => Json(new { ok = false, message = Loc.T("ProjectsController.DispatchPocFeedback") }),
            PocFeedbackDispatchStatus.InvalidSelection => Json(new { ok = false, reload = true, message = Loc.T("ProjectsController.DispatchPocFeedback.2") }),
            PocFeedbackDispatchStatus.RequirementFailed => Json(new { ok = false, message = RequirementErrorMessage(report.RequirementError) }),
            PocFeedbackDispatchStatus.FixFailed => Json(new { ok = false, message = FixErrorMessage(report.FixError) }),
            PocFeedbackDispatchStatus.PocAccepted => Json(new { ok = false, reload = true, message = PocAcceptanceGate.LockedMessage }),
            _ => NotFound(Loc.T("ProjectsController.DispatchPocFeedback.3"))
        };
    }

    // Đường tài liệu ĐÈ đường chỉnh demo trong cùng một lượt: POC sẽ được dựng lại từ tài liệu đã sửa nên
    // vá HTML lúc này vừa phí một vòng trong trần, vừa cho ra bản vá bị bỏ đi ngay. Các ghi chú thẩm mỹ
    // được HOÃN tới vòng dựng mới bản demo — phải nói rõ điều đó, nếu không người dùng tưởng chúng đã đi.
    private static string DispatchOkMessage(PocFeedbackDispatchReport report)
    {
        if (report.RoutedCount == 0)
            return Loc.T("ProjectsController.DispatchOkMessage", report.FixSentCount);

        var message = Loc.T("ProjectsController.DispatchOkMessage.2", report.RoutedCount);

        if (report.HeldCount > 0)
            message += Loc.T("ProjectsController.DispatchOkMessage.3", report.HeldCount);

        return message;
    }

    private static string RequirementErrorMessage(RoutePocFeedbackResult? error) => error switch
    {
        RoutePocFeedbackResult.BaNotConfigured => Loc.T("ProjectsController.RequirementErrorMessage"),
        RoutePocFeedbackResult.ComposeFailed => Loc.T("ProjectsController.RequirementErrorMessage.2"),
        RoutePocFeedbackResult.NoOpenComments => Loc.T("ProjectsController.RequirementErrorMessage.3"),
        RoutePocFeedbackResult.DesignFailed => Loc.T("ProjectsController.RequirementErrorMessage.5"),
        _ => Loc.T("ProjectsController.RequirementErrorMessage.4")
    };

    private static string FixErrorMessage(RequestStageRevisionResult? error) => error switch
    {
        RequestStageRevisionResult.RevisionLimitReached => Loc.T("ProjectsController.FixErrorMessage", DeliveryPipeline.MaxRevisionRounds),
        RequestStageRevisionResult.StageMismatch => Loc.T("ProjectsController.FixErrorMessage.2"),
        RequestStageRevisionResult.MissingFeedback => Loc.T("ProjectsController.FixErrorMessage.3"),
        _ => Loc.T("ProjectsController.FixErrorMessage.4")
    };

    // NGHIỆM THU BẢN DEMO: điểm dừng của hành trình phía người yêu cầu. Trước đây họ chỉ có các đường
    // "còn sai chỗ này" (ghim ghi chú / nhờ Dev chỉnh / gửi về Requirement) mà không có đường nào nói
    // "được rồi", nên đội delivery phải đi hỏi miệng và chặng cuối stepper không bao giờ đóng.
    // KHÔNG đẩy pipeline: chỉ ghi lại ai/lúc nào + báo cho người có quyền duyệt (xem AcceptPocUseCase).
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Message = "Project.NotFound")]
    public async Task<IActionResult> AcceptPoc(Guid projectId)
    {
        var result = await _acceptPocUseCase.ExecuteAsync(
            projectId, User.Identity?.Name ?? string.Empty, HttpContext.RequestAborted);

        // Chuỗi ở đây là chữ hiện TRONG hộp xác nhận nghiệm thu của trang POC Review, và hộp đó để
        // tiếng Anh cho khớp nhãn nút trên command bar ("Approve POC" / "Withdraw Approve") — xem
        // bindAcceptanceButton trong poc-review.js. Đây là ngoại lệ có chủ đích của quy ước tiếng Việt.
        return result switch
        {
            AcceptPocResult.Ok => Json(new { ok = true, message = "The demo is marked as approved — the delivery team has been notified to move on." }),
            AcceptPocResult.AlreadyAccepted => Json(new { ok = false, message = "This demo has already been approved." }),
            AcceptPocResult.NoPoc => Json(new { ok = false, message = "There is no demo to approve yet." }),
            _ => NotFound(Loc.T("ProjectsController.AcceptPoc"))
        };
    }

    // RÚT NGHIỆM THU: cửa mở khoá DUY NHẤT. Nghiệm thu đóng băng chat BA + ghi chú POC (xem
    // PocAcceptanceGate), nên nếu chỉ có chiều đi thì người dùng phát hiện thêm điểm sai sau khi bấm
    // "được rồi" sẽ không còn đường nào để nói. Cùng quyền với chiều đi (RequirementsManage): ai nghiệm
    // thu được thì rút được — khoá này thuộc về phía người yêu cầu, không phải một cổng của delivery.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(AppPermission.RequirementsManage)]
    [RequireProjectAccess(Message = "Project.NotFound")]
    public async Task<IActionResult> WithdrawPocAcceptance(Guid projectId)
    {
        var result = await _withdrawPocAcceptanceUseCase.ExecuteAsync(
            projectId, User.Identity?.Name ?? string.Empty, HttpContext.RequestAborted);

        // Tiếng Anh vì cùng lý do với AcceptPoc ở trên: chữ này hiện trong hộp xác nhận của POC Review.
        return result switch
        {
            WithdrawPocAcceptanceResult.Ok => Json(new { ok = true, message = "Approval withdrawn — chat and notes are open again; the delivery team has been notified." }),
            WithdrawPocAcceptanceResult.NotAccepted => Json(new { ok = false, message = "This demo is not in the approved state." }),
            _ => NotFound(Loc.T("ProjectsController.WithdrawPocAcceptance"))
        };
    }

    // Packages the agent-generated multi-file app (04_Implementation/src) into a .zip the user can
    // download — the only way to actually get the produced source out of the workspace.
    [RequireProjectAccess(Message = "Project.NoSourceCode")]
    public async Task<IActionResult> DownloadSource(Guid projectId)
    {
        var result = await _getImplementationSourceQuery.ExecuteAsync(projectId);
        if (result == null)
            return NotFound(Loc.T("ProjectsController.DownloadSource"));

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
