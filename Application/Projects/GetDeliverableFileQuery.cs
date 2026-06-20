using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public record DeliverableFileContent(string FilePath, string FileName, bool TextPreviewable);

/// <summary>
/// Phân giải an toàn một file deliverable theo đường dẫn tương đối trong workspace của project,
/// chống path-traversal qua <see cref="WorkspacePathResolver.GetSafeFullPath"/>. Trả null nếu
/// project không tồn tại, đường dẫn vượt ra ngoài workspace, hoặc file không có thật.
/// </summary>
public class GetDeliverableFileQuery
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _resolver;

    public GetDeliverableFileQuery(AppDbContext db, WorkspacePathResolver resolver)
    {
        _db = db;
        _resolver = resolver;
    }

    public async Task<DeliverableFileContent?> ExecuteAsync(Guid projectId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId);
        if (project == null)
            return null;

        var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
        var workspacePath = _resolver.GetProjectWorkspacePath(projectKey);

        string fullPath;
        try
        {
            fullPath = _resolver.GetSafeFullPath(workspacePath, relativePath);
        }
        catch (InvalidOperationException)
        {
            return null; // cố tình thoát ra ngoài workspace
        }

        if (!File.Exists(fullPath))
            return null;

        return new DeliverableFileContent(fullPath, Path.GetFileName(fullPath), DeliverableFileTypes.IsTextPreviewable(fullPath));
    }
}
