using ICOGenerator.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public record MockupFileResult(string FilePath);

public class GetMockupFileQuery
{
    private readonly IAppDbContext _db;
    private readonly IWorkspacePathResolver _workspacePathResolver;

    public GetMockupFileQuery(IAppDbContext db, IWorkspacePathResolver workspacePathResolver)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
    }

    public async Task<MockupFileResult?> ExecuteAsync(Guid projectId)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId);
        if (project == null)
            return null;

        var filePath = _workspacePathResolver.GetMockupPath(project.Name);
        return File.Exists(filePath) ? new MockupFileResult(filePath) : null;
    }
}
