using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public record RequirementWorkspaceResult(Project Project, string SelectedVersion);

public class GetRequirementWorkspaceQuery
{
    private readonly AppDbContext _db;

    public GetRequirementWorkspaceQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<RequirementWorkspaceResult?> ExecuteAsync(Guid projectId, string? version = null)
    {
        // Chỉ đọc để render màn hình workspace (controller trả thẳng vào View, không SaveChanges trên đồ
        // thị này) ⇒ AsNoTracking để khỏi tốn change-tracker cho cả Project + Documents + Conversations +
        // WorkflowRuns được Include bên dưới.
        var project = await _db.Projects
            .AsNoTracking()
            .Include(x => x.Documents)
            .Include(x => x.Conversations.OrderBy(c => c.CreatedAt))
                .ThenInclude(x => x.Agent)
            .Include(x => x.WorkflowRuns.OrderBy(w => w.CreatedAt))
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
