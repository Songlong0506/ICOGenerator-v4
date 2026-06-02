using ICOGenerator.Application.Abstractions;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.ViewModels;

namespace ICOGenerator.Application.Projects;

public class CreateProjectUseCase
{
    private readonly IAppDbContext _db;

    public CreateProjectUseCase(IAppDbContext db)
    {
        _db = db;
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
        return project.Id;
    }
}
