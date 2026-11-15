using ICOGenerator.Localization;
using ICOGenerator.Application.Projects;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace ICOGenerator.Controllers;

// "Thử model khác" của trang POC Review: dựng lại bản demo bằng một model khác cho agent Developer, trong
// thư mục riêng, rồi so bằng số liệu (xem PocVariant). Controller riêng thay vì thêm vào ProjectsController
// để quyền của cả bề mặt này khai ĐÚNG MỘT chỗ: hai attribute xếp chồng = cần ĐỦ cả hai (AND) — xem dự án
// được, và có quyền thí nghiệm model (mặc định chỉ Admin; tiêu tiền thật). Không gắn RequirementsManage/
// DeliveryAdvance: thí nghiệm không đổi gì của dự án nên không cần quyền điều khiển pipeline.
[RequirePermission(AppPermission.ProjectsView)]
[RequirePermission(AppPermission.PocModelExperiment)]
public class PocVariantsController : Controller
{
    private readonly ListPocVariantsQuery _listQuery;
    private readonly StartPocVariantUseCase _startUseCase;
    private readonly DeletePocVariantUseCase _deleteUseCase;
    private readonly GetPocVariantFileQuery _fileQuery;

    public PocVariantsController(ListPocVariantsQuery listQuery, StartPocVariantUseCase startUseCase,
        DeletePocVariantUseCase deleteUseCase, GetPocVariantFileQuery fileQuery)
    {
        _listQuery = listQuery;
        _startUseCase = startUseCase;
        _deleteUseCase = deleteUseCase;
        _fileQuery = fileQuery;
    }

    [HttpGet]
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> List(Guid projectId)
    {
        var page = await _listQuery.ExecuteAsync(projectId, HttpContext.RequestAborted);
        return page == null
            ? Json(new { ok = false, error = Loc.T("PocVariantsController.List") })
            : Json(new { ok = true, page });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> Start(Guid projectId, Guid aiModelId)
    {
        var result = await _startUseCase.ExecuteAsync(projectId, aiModelId, User.Identity?.Name, HttpContext.RequestAborted);
        var error = result switch
        {
            StartPocVariantResult.Ok => null,
            StartPocVariantResult.ModelNotFound => Loc.T("PocVariantsController.Start"),
            StartPocVariantResult.NoDeveloperAgent => Loc.T("PocVariantsController.Start.2"),
            StartPocVariantResult.NoSourcePoc => Loc.T("PocVariantsController.Start.3"),
            StartPocVariantResult.AlreadyRunning => Loc.T("PocVariantsController.Start.4"),
            StartPocVariantResult.LimitReached => Loc.T("PocVariantsController.Start.5", StartPocVariantUseCase.MaxVariantsPerProject),
            _ => Loc.T("PocVariantsController.Start.6")
        };
        return Json(new { ok = error == null, error });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> Delete(Guid projectId, Guid variantId)
    {
        var result = await _deleteUseCase.ExecuteAsync(projectId, variantId, HttpContext.RequestAborted);
        var error = result switch
        {
            DeletePocVariantResult.Ok => null,
            DeletePocVariantResult.Running => Loc.T("PocVariantsController.Delete"),
            _ => Loc.T("PocVariantsController.Delete.2")
        };
        return Json(new { ok = error == null, error });
    }

    // Bản demo của một biến thể — phục vụ qua ĐÚNG đường với bản chính (PocDemoResponse: CSP sandbox, cắt
    // khối hướng dẫn agent), nên HTML do model lạ sinh ra không có rào chắn nào lỏng hơn.
    [HttpGet]
    [RequireProjectAccess(Message = "PocVariant.DemoNotFound")]
    public async Task<IActionResult> Demo(Guid projectId, Guid variantId)
    {
        var path = await _fileQuery.ExecuteAsync(projectId, variantId, HttpContext.RequestAborted);
        if (path == null)
            return NotFound(Loc.T("PocVariantsController.Demo"));

        return await PocDemoResponse.BuildAsync(this, path, review: false, HttpContext.RequestAborted);
    }
}
