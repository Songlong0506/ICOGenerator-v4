using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public record PocReviewPage(Guid ProjectId, string ProjectName, bool HasMockup);

/// <summary>Dữ liệu cho trang review POC (Projects/PocReview): tên project + POC đã tồn tại chưa.</summary>
public class GetPocReviewQuery
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _workspacePathResolver;

    public GetPocReviewQuery(AppDbContext db, WorkspacePathResolver workspacePathResolver)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
    }

    public async Task<PocReviewPage?> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return null;

        var mockupPath = _workspacePathResolver.GetMockupPath(
            WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));

        return new PocReviewPage(project.Id, project.Name, File.Exists(mockupPath));
    }
}
