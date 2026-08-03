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

    /// <summary>
    /// Đường dẫn bản POC để phục vụ. <paramref name="version"/> null ⇒ bản HIỆN TẠI
    /// (<c>poc-demo.html</c>); có giá trị ⇒ bản chụp của vòng dựng đó trong <c>poc-history/</c> (xem
    /// <see cref="PocSnapshots"/>) — đường để người nghiệm thu mở lại bản họ đã xem ở vòng trước.
    /// Số vòng chỉ được dùng để TRA trong danh sách file có thật, không bao giờ ghép thẳng vào đường
    /// dẫn, nên không mở ra lối đi vòng khỏi workspace.
    /// </summary>
    public async Task<MockupFileResult?> ExecuteAsync(Guid projectId, int? version = null)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId);
        if (project == null)
            return null;

        var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);

        if (version is { } v)
        {
            var snapshot = PocSnapshots.TryGetPath(_workspacePathResolver.GetProjectWorkspacePath(projectKey), v);
            return snapshot != null ? new MockupFileResult(snapshot) : null;
        }

        var filePath = _workspacePathResolver.GetMockupPath(projectKey);
        return File.Exists(filePath) ? new MockupFileResult(filePath) : null;
    }
}
