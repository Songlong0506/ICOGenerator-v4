using ICOGenerator.Data;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum RemoveProjectMemberResult
{
    Removed,
    NotFound,

    /// <summary>Người gọi không phải chủ project và không có quyền xem-tất-cả.</summary>
    NotAllowed
}

/// <summary>Gỡ một thành viên khỏi project. Cùng luật với AddProjectMemberUseCase: chủ project hoặc ProjectsViewAll.</summary>
public class RemoveProjectMemberUseCase
{
    private readonly AppDbContext _db;

    public RemoveProjectMemberUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<RemoveProjectMemberResult> ExecuteAsync(
        Guid memberId, string? actorUsername, bool canManageAll, CancellationToken cancellationToken = default)
    {
        var member = await _db.ProjectMembers
            .Include(m => m.Project)
            .FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);

        if (member == null)
            return RemoveProjectMemberResult.NotFound;

        var ownerUsername = member.Project?.CreatedByUsername;
        if (!canManageAll && (ownerUsername == null || ownerUsername != actorUsername))
            return RemoveProjectMemberResult.NotAllowed;

        _db.ProjectMembers.Remove(member);
        await _db.SaveChangesAsync(cancellationToken);
        return RemoveProjectMemberResult.Removed;
    }
}
