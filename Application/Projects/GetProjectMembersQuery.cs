using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public record ProjectMemberVm(Guid Id, string Username, string? DisplayName, string? AddedByUsername, DateTime CreatedAt);

/// <summary>Danh sách thành viên của một project + quyền quản lý của người đang xem, cho modal "Chia sẻ".</summary>
public record ProjectMembersVm(string? OwnerUsername, bool CanManage, IReadOnlyList<ProjectMemberVm> Members);

public class GetProjectMembersQuery
{
    private readonly AppDbContext _db;

    public GetProjectMembersQuery(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ProjectMembersVm?> ExecuteAsync(Guid projectId, string? actorUsername, bool canManageAll, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.CreatedByUsername })
            .FirstOrDefaultAsync(cancellationToken);

        if (project == null)
            return null;

        // Tra DisplayName bằng join sang AppUsers; user đã bị xóa/đổi tên thì DisplayName null, vẫn hiển thị username.
        var members = await _db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ProjectMemberVm(
                m.Id,
                m.Username,
                _db.AppUsers.Where(u => u.Username == m.Username).Select(u => u.DisplayName).FirstOrDefault(),
                m.AddedByUsername,
                m.CreatedAt))
            .ToListAsync(cancellationToken);

        // Chỉ chủ project (hoặc người xem-tất-cả như TeamDev/Admin) được thêm/xóa thành viên.
        var canManage = canManageAll
            || (project.CreatedByUsername != null && project.CreatedByUsername == actorUsername);

        return new ProjectMembersVm(project.CreatedByUsername, canManage, members);
    }
}
