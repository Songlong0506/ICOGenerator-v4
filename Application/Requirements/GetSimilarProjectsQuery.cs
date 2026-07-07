using ICOGenerator.Data;
using ICOGenerator.Services.Requirements.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>Một dự án tương tự cho panel ở trang Requirements (JSON cho JS render).</summary>
public record SimilarProjectItemVm(
    Guid ProjectId,
    string ProjectName,
    string? OrgUnitCode,
    IReadOnlyList<string> MatchedDocuments,
    string Snippet);

/// <summary>
/// Panel "Dự án tương tự" của trang Requirements: hỏi <see cref="ProjectKnowledgeService"/> với truy
/// vấn = tên + mô tả dự án + các lượt user gần nhất — cùng nguồn tri thức mà BA dùng trong prompt,
/// nhưng đưa THẲNG cho người dùng thấy để tham khảo/tránh làm trùng. Fail-open: lỗi ⇒ danh sách rỗng.
/// </summary>
public class GetSimilarProjectsQuery
{
    // Cùng số lượt user mà BARequirementService dùng cho truy vấn tri thức — panel và prompt nhìn
    // cùng một "góc truy xuất".
    private const int RecentUserTurns = 3;

    private readonly AppDbContext _db;
    private readonly ProjectKnowledgeService _knowledge;

    public GetSimilarProjectsQuery(AppDbContext db, ProjectKnowledgeService knowledge)
    {
        _db = db;
        _knowledge = knowledge;
    }

    public async Task<IReadOnlyList<SimilarProjectItemVm>> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return Array.Empty<SimilarProjectItemVm>();

        var recentUserMessages = await _db.AgentConversations.AsNoTracking()
            .Where(c => c.ProjectId == projectId && c.Role == "user")
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Take(RecentUserTurns)
            .Select(c => c.Message)
            .ToListAsync(cancellationToken);

        var similar = await _knowledge.FindSimilarProjectsAsync(
            project.Id, project.Name, project.Description, project.OrgUnitCode,
            string.Join("\n", recentUserMessages), cancellationToken: cancellationToken);

        return similar
            .Select(p => new SimilarProjectItemVm(p.ProjectId, p.ProjectName, p.OrgUnitCode, p.MatchedDocuments, p.Snippet))
            .ToList();
    }
}
