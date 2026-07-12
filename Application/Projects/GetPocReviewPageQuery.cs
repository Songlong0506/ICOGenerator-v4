using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public record PocReviewPageVm(Domain.Project Project, bool HasMockup);

/// <summary>Dữ liệu cho trang POC Review: project + đã có file mockup chưa (chưa có thì trang chỉ báo trống).</summary>
public class GetPocReviewPageQuery
{
    private readonly AppDbContext _db;
    private readonly WorkspacePathResolver _workspacePathResolver;

    public GetPocReviewPageQuery(AppDbContext db, WorkspacePathResolver workspacePathResolver)
    {
        _db = db;
        _workspacePathResolver = workspacePathResolver;
    }

    public async Task<PocReviewPageVm?> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return null;

        var mockupPath = _workspacePathResolver.GetMockupPath(
            WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));

        return new PocReviewPageVm(project, File.Exists(mockupPath));
    }
}
