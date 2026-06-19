using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public record MockupFileResult(string FilePath);

public class GetMockupFileQuery
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _workspacePathResolver;

    public GetMockupFileQuery(AppDbContext db, WorkspacePathResolver workspacePathResolver)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
    }

    public async Task<MockupFileResult?> ExecuteAsync(Guid projectId)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId);
        if (project == null)
            return null;

        var filePath = _workspacePathResolver.GetMockupPath(WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));
        return File.Exists(filePath) ? new MockupFileResult(filePath) : null;
    }
}
