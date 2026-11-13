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
            ? Json(new { ok = false, error = "Project không tồn tại." })
            : Json(new { ok = true, page });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]
    public async Task<IActionResult> Start(Guid projectId, Guid aiModelId, PocVariantHarness harness = PocVariantHarness.Builtin)
    {
        var result = await _startUseCase.ExecuteAsync(projectId, aiModelId, User.Identity?.Name, harness, HttpContext.RequestAborted);
        var error = result switch
        {
            StartPocVariantResult.Ok => null,
            StartPocVariantResult.ModelNotFound => "Model không tồn tại hoặc đang tắt ở màn AI Models.",
            StartPocVariantResult.NoDeveloperAgent => "Chưa có agent Developer.",
            StartPocVariantResult.NoSourcePoc => "Dự án chưa có lượt dựng POC hoàn tất nào để lấy đầu vào.",
            StartPocVariantResult.AlreadyRunning => "Đang có một lượt thử chờ/chạy cho dự án này — đợi nó xong rồi thử tiếp.",
            StartPocVariantResult.LimitReached => $"Đã giữ đủ {StartPocVariantUseCase.MaxVariantsPerProject} biến thể — xoá bớt kết quả cũ rồi thử tiếp.",
            StartPocVariantResult.HarnessDisabled => "Vỏ opencode chưa được bật trên máy chủ (PocOpencode:Enabled).",
            _ => "Project không tồn tại."
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
            DeletePocVariantResult.Running => "Lượt thử đang chạy — đợi nó xong rồi mới xoá được.",
            _ => "Không tìm thấy biến thể."
        };
        return Json(new { ok = error == null, error });
    }

    // Bản demo của một biến thể — phục vụ qua ĐÚNG đường với bản chính (PocDemoResponse: CSP sandbox, cắt
    // khối hướng dẫn agent), nên HTML do model lạ sinh ra không có rào chắn nào lỏng hơn.
    [HttpGet]
    [RequireProjectAccess(Message = "Không tìm thấy bản demo của biến thể.")]
    public async Task<IActionResult> Demo(Guid projectId, Guid variantId)
    {
        var path = await _fileQuery.ExecuteAsync(projectId, variantId, HttpContext.RequestAborted);
        if (path == null)
            return NotFound("Không tìm thấy bản demo của biến thể.");

        return await PocDemoResponse.BuildAsync(this, path, review: false, HttpContext.RequestAborted);
    }
}
