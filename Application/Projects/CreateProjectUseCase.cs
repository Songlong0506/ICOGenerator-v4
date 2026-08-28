using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum CreateProjectResult
{
    Created,
    NameRequired,
    /// <summary>Chưa chọn đơn vị yêu cầu, hoặc mã gửi lên không có thật trong OrgUnits.</summary>
    OrgUnitRequired
}

/// <summary>Kết quả tạo project — <see cref="ProjectId"/> chỉ có giá trị khi <see cref="Result"/> = Created.</summary>
public record CreateProjectOutcome(CreateProjectResult Result, Guid ProjectId);

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

    public async Task<CreateProjectOutcome> ExecuteAsync(ProjectCreateVm vm, string? createdByUsername = null)
    {
        var name = (vm.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return new CreateProjectOutcome(CreateProjectResult.NameRequired, Guid.Empty);

        // Đơn vị yêu cầu là BẮT BUỘC: mọi thứ phía sau (ghi chú đơn vị nạp cho BA, tên phòng ban thật trong
        // tài liệu, roll-up department ở trang Usage) đều đọc từ đây, nên một project không có đơn vị là
        // một lỗ hổng dữ liệu kéo dài cả vòng đời dự án. Chỉ nhận mã CÓ THẬT trong OrgUnits — dropdown
        // render từ DB nên mã lạ/đã xóa mềm chỉ đến từ request tự chế, và lần này thì CHẶN thay vì bỏ qua.
        var code = (vm.OrgUnitCode ?? string.Empty).Trim();
        if (code.Length == 0 || !await _db.OrgUnits.AnyAsync(u => !u.IsDelete && u.OrgUnitCode == code))
            return new CreateProjectOutcome(CreateProjectResult.OrgUnitRequired, Guid.Empty);

        // Chỉ lưu Name + Description + đơn vị yêu cầu. Generation Mode và Backend/Frontend Git để trống —
        // TeamDev điền sau ở Agent Dashboard (UpdateDeliveryConfigUseCase) khi pipeline cần tới chúng.
        var project = new Project
        {
            Name = name,
            Description = (vm.Description ?? string.Empty).Trim(),
            // Gắn chủ sở hữu để trang Projects/Index lọc đúng: User thường chỉ thấy project của mình.
            CreatedByUsername = createdByUsername,
            OrgUnitCode = code
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

        return new CreateProjectOutcome(CreateProjectResult.Created, project.Id);
    }
}
