using ICOGenerator.Application.Abstractions;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Application.Projects;

public class CreateProjectUseCase
{
    private readonly IAppDbContext _db;

    public CreateProjectUseCase(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> ExecuteAsync(CreateProjectCommand command)
    {
        var project = new Project
        {
            Name = command.Name,
            Description = command.Description,
            GenerationMode = command.GenerationMode,
            BackendGitUrl = command.BackendGitUrl,
            FrontendGitUrl = command.FrontendGitUrl,
            Status = ProjectStatus.Planning
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        return project.Id;
    }
}
