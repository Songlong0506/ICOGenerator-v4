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

    public async Task<IReadOnlyList<ProjectListItem>> ExecuteAsync()
    {
        var projects = await _db.Projects
            .Include(x => x.WorkflowRuns)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();

        return projects
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
    }
}
