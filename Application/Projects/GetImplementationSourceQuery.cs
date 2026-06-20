using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public record ImplementationSourceResult(string ZipFilePath, string DownloadFileName);

/// <summary>
/// Packages the multi-file application the Developer agent writes into <c>04_Implementation/src/</c>
/// as a downloadable .zip — the only way to get the produced source out of the workspace. Returns
/// null when the project or its generated source does not exist.
/// </summary>
public class GetImplementationSourceQuery
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _workspacePathResolver;
    private readonly ImplementationSourcePackager _packager;

    public GetImplementationSourceQuery(
        AppDbContext db,
        WorkspacePathResolver workspacePathResolver,
        ImplementationSourcePackager packager)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
        _packager = packager;
    }

    public async Task<ImplementationSourceResult?> ExecuteAsync(Guid projectId)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId);
        if (project == null)
            return null;

        var projectKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name);
        var sourcePath = _workspacePathResolver.GetImplementationSourcePath(projectKey);

        var zipPath = await _packager.CreateArchiveAsync(sourcePath);
        if (zipPath == null)
            return null;

        var downloadName = $"{WorkspacePathResolver.MakeSafeFolderName(project.Name)}-source.zip";
        return new ImplementationSourceResult(zipPath, downloadName);
    }
}
