using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public class CreateProjectUseCase
{
    private readonly AppDbContext _db;
    private readonly IArtifactStorage _artifactStorage;
    private readonly ILogger<CreateProjectUseCase> _logger;

    public CreateProjectUseCase(
        AppDbContext db,
        IArtifactStorage artifactStorage,
        ILogger<CreateProjectUseCase> logger)
    {
        _db = db;
        _artifactStorage = artifactStorage;
        _logger = logger;
    }

    public async Task<Guid> ExecuteAsync(ProjectCreateVm vm, string? createdByUsername = null)
    {
        // Đơn vị yêu cầu (tùy chọn): chỉ lưu khi mã CÓ THẬT trong OrgUnits — dropdown render từ DB nên mã
        // lạ chỉ đến từ request tự chế; lặng lẽ bỏ qua thay vì chặn việc tạo project vì một field phụ.
        string? orgUnitCode = null;
        if (!string.IsNullOrWhiteSpace(vm.OrgUnitCode))
        {
            var code = vm.OrgUnitCode.Trim();
            if (await _db.OrgUnits.AnyAsync(u => !u.IsDelete && u.OrgUnitCode == code))
                orgUnitCode = code;
        }

        // Chỉ lưu Name + Description (+ đơn vị yêu cầu nếu chọn). Generation Mode và Backend/Frontend Git
        // để trống — TeamDev điền sau ở Agent Dashboard (UpdateDeliveryConfigUseCase) khi pipeline cần tới chúng.
        var project = new Project
        {
            Name = vm.Name,
            Description = vm.Description,
            // Gắn chủ sở hữu để trang Projects/Index lọc đúng: User thường chỉ thấy project của mình.
            CreatedByUsername = createdByUsername,
            OrgUnitCode = orgUnitCode
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        // Tạo khung thư mục giai đoạn trên đĩa. Best-effort: RootPath cấu hình sai trên máy này
        // vẫn không chặn việc tạo project.
        try
        {
            _artifactStorage.InitializeProjectWorkspace(WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not initialize workspace folders for project {ProjectName}.", project.Name);
        }

        return project.Id;
    }
}
