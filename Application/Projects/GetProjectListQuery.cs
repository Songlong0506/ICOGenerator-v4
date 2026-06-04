using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public class GetProjectListQuery
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _workspacePathResolver;

    public GetProjectListQuery(AppDbContext db, WorkspacePathResolver workspacePathResolver)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
    }

    public async Task<IReadOnlyList<ProjectListItem>> ExecuteAsync()
    {
        var projects = await _db.Projects
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();

        return projects
            .Select(project => new ProjectListItem(project, File.Exists(_workspacePathResolver.GetMockupPath(project.Name))))
            .ToList();
    }
}
