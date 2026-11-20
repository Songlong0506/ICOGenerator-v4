using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum UpdateDeliveryConfigResult
{
    Updated,
    ProjectNotFound,
    InvalidBaseBranch
}

/// <summary>
/// Cập nhật cấu hình delivery (Generation Mode, Backend/Frontend Git, nhánh đích, tài khoản Git) cho một
/// project. Dùng ở Agent Dashboard, chỉ TeamDev/Admin (quyền DeliveryAdvance) gọi được — đây là chỗ team kỹ
/// thuật điền các field mà end-user không cần biết lúc tạo project.
/// </summary>
public class UpdateDeliveryConfigUseCase
{
    private readonly AppDbContext _db;

    public UpdateDeliveryConfigUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<UpdateDeliveryConfigResult> ExecuteAsync(UpdateDeliveryConfigVm vm)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == vm.ProjectId);
        if (project == null)
            return UpdateDeliveryConfigResult.ProjectNotFound;

        // Chặn ở form chứ không để tới bước PR: tên nhánh thành đối số của git (`git init -b`, `checkout`),
        // và một giá trị sai chỉ lộ ra ở bước CUỐI pipeline.
        var baseBranch = Normalize(vm.GitBaseBranch);
        if (baseBranch != null && !ProjectGitSettings.IsValidBranchName(baseBranch))
            return UpdateDeliveryConfigResult.InvalidBaseBranch;

        // Generation Mode luôn có giá trị (true/false); mặc định true nếu form không post giá trị nào.
        project.IsUseBoschTemplate = vm.IsUseBoschTemplate;

        // Chuẩn hóa chuỗi rỗng/khoảng trắng về null để "chưa nhập" và "" là một trạng thái duy nhất.
        project.BackendGitUrl = Normalize(vm.BackendGitUrl);
        project.FrontendGitUrl = Normalize(vm.FrontendGitUrl);
        project.GitBaseBranch = baseBranch;
        project.GitUsername = Normalize(vm.GitUsername);

        // Token: tích "xoá" thắng; ô trống = giữ token đã lưu (trang không render lại token cũ).
        if (vm.ClearGitAccessToken)
            project.GitAccessToken = null;
        else if (Normalize(vm.GitAccessToken) is { } token)
            project.GitAccessToken = token;

        await _db.SaveChangesAsync();
        return UpdateDeliveryConfigResult.Updated;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
