using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;

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

    public async Task<Guid> ExecuteAsync(ProjectCreateVm vm)
    {
        var project = new Project
        {
            Name = vm.Name,
            Description = vm.Description,
            GenerationMode = vm.GenerationMode,
            BackendGitUrl = vm.BackendGitUrl,
            FrontendGitUrl = vm.FrontendGitUrl,
            Status = ProjectStatus.Planning
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
