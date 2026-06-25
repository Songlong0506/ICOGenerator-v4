using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

public record RequirementWorkspaceResult(Project Project, string SelectedVersion, bool BaModelSupportsVision);

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
            .Include(x => x.SourceFiles.OrderByDescending(s => s.CreatedAt))
            .FirstOrDefaultAsync(x => x.Id == projectId);

        if (project == null)
            return null;

        // Cờ vision của model BA đang cấu hình: dùng để cảnh báo trên UI rằng ảnh sẽ KHÔNG được model đọc
        // (chỉ phần text của PDF được dùng) khi model hiện tại không hỗ trợ vision.
        var baSupportsVision = await _db.Agents
            .AsNoTracking()
            .Where(a => a.RoleKey == AgentRoleKey.BusinessAnalyst && a.AiModel != null)
            .Select(a => a.AiModel!.SupportsVision)
            .FirstOrDefaultAsync();

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

        return new RequirementWorkspaceResult(project, selectedVersion ?? "draft", baSupportsVision);
    }
}
