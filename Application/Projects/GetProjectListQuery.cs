using ICOGenerator.Data;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Domain.Enums;
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

    public const int DefaultPageSize = 10;

    public async Task<ProjectListPage> ExecuteAsync(int page = 1, int pageSize = DefaultPageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = DefaultPageSize;

        var baseQuery = _db.Projects.OrderByDescending(x => x.CreatedAt);

        var totalCount = await baseQuery.CountAsync();

        var projects = await baseQuery
            .Include(x => x.WorkflowRuns)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = projects
            .Select(project =>
            {
                var latestWorkflow = project.WorkflowRuns
                    .OrderByDescending(x => x.CreatedAt)
                    .FirstOrDefault();
                var hasRunningWorkflow = latestWorkflow is not null
                    && latestWorkflow.Status is not WorkflowRunStatus.Completed
                        and not WorkflowRunStatus.Failed
                        and not WorkflowRunStatus.Canceled;

                return new ProjectListItem(
                    project,
                    File.Exists(_workspacePathResolver.GetMockupPath(project.Name)),
                    latestWorkflow?.Status.ToString(),
                    latestWorkflow?.CurrentStage.ToString(),
                    hasRunningWorkflow);
            })
            .ToList();

        return new ProjectListPage(items, page, pageSize, totalCount);
    }
}
