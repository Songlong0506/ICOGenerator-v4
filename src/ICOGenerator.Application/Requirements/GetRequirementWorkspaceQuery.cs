using ICOGenerator.Application.Abstractions;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public record RequirementWorkspaceResult(Project Project, string SelectedVersion);

public class GetRequirementWorkspaceQuery
{
    private readonly IAppDbContext _db;

    public GetRequirementWorkspaceQuery(IAppDbContext db)
    {
        _db = db;
    }

    public async Task<RequirementWorkspaceResult?> ExecuteAsync(Guid projectId, string? version = null)
    {
        var project = await _db.Projects
            .Include(x => x.Documents)
            .Include(x => x.Conversations.OrderBy(c => c.CreatedAt))
                .ThenInclude(x => x.Agent)
            .FirstOrDefaultAsync(x => x.Id == projectId);

        if (project == null)
            return null;

        var selectedVersion = version;
        if (string.IsNullOrWhiteSpace(selectedVersion))
        {
            selectedVersion = project.Documents.Any(x => x.VersionName == "draft")
                ? "draft"
                : project.Documents
                    .Where(x => x.VersionName.StartsWith("V"))
                    .OrderByDescending(x => int.TryParse(x.VersionName.Replace("V", ""), out var n) ? n : 0)
                    .Select(x => x.VersionName)
                    .FirstOrDefault();
        }

        return new RequirementWorkspaceResult(project, selectedVersion ?? "draft");
    }
}
