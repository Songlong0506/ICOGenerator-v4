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

        var baseQuery = _db.Projects.AsNoTracking().OrderByDescending(x => x.CreatedAt);

        var totalCount = await baseQuery.CountAsync();

        // Lấy status/stage của workflow run MỚI NHẤT bằng subquery ở DB thay vì Include toàn bộ
        // WorkflowRuns về RAM — tránh kéo dữ liệu thừa với project chạy nhiều lần.
        var rows = await baseQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(project => new
            {
                Project = project,
                LatestWorkflow = project.WorkflowRuns
                    .OrderByDescending(w => w.CreatedAt)
                    .Select(w => new { w.Status, w.CurrentStage })
                    .FirstOrDefault()
            })
            .ToListAsync();

        var items = rows
            .Select(row =>
            {
                var latestWorkflow = row.LatestWorkflow;
                var hasRunningWorkflow = latestWorkflow is not null
                    && latestWorkflow.Status is not WorkflowRunStatus.Completed
                        and not WorkflowRunStatus.Failed
                        and not WorkflowRunStatus.Canceled;

                var projectKey = WorkspacePathResolver.GetWorkspaceFolder(row.Project.Id, row.Project.Name);

                return new ProjectListItem(
                    row.Project,
                    File.Exists(_workspacePathResolver.GetMockupPath(projectKey)),
                    latestWorkflow?.Status.ToString(),
                    latestWorkflow?.CurrentStage.ToString(),
                    hasRunningWorkflow);
            })
            .ToList();

        return new ProjectListPage(items, page, pageSize, totalCount);
    }
}
