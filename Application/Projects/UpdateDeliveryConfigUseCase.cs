using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum UpdateDeliveryConfigResult
{
    Updated,        // đã lưu cấu hình
    ProjectNotFound // không tìm thấy project
}

/// <summary>
/// Cập nhật cấu hình delivery (Generation Mode, Backend/Frontend Git) cho một project. Dùng ở Agent
/// Dashboard, chỉ TeamDev/Admin (quyền DeliveryAdvance) gọi được — đây là chỗ team kỹ thuật điền các
/// field mà end-user không cần biết lúc tạo project.
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

        // null được giữ nguyên là null (chưa chọn) — cổng Approve sẽ chặn nếu cần tới mà chưa có.
        project.IsUseBoschTemplate = vm.IsUseBoschTemplate;

        // Chuẩn hóa chuỗi rỗng/khoảng trắng về null để "chưa nhập" và "" là một trạng thái duy nhất.
        project.BackendGitUrl = NormalizeUrl(vm.BackendGitUrl);
        project.FrontendGitUrl = NormalizeUrl(vm.FrontendGitUrl);

        await _db.SaveChangesAsync();
        return UpdateDeliveryConfigResult.Updated;
    }

    private static string? NormalizeUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : url.Trim();
}
