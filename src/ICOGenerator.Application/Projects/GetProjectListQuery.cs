using ICOGenerator.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public class GetProjectListQuery
{
    private readonly IAppDbContext _db;
    private readonly IWorkspacePathResolver _workspacePathResolver;

    public GetProjectListQuery(IAppDbContext db, IWorkspacePathResolver workspacePathResolver)
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
