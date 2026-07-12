using ICOGenerator.Data;
using ICOGenerator.Domain;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum AddProjectMemberResult
{
    Added,
    ProjectNotFound,

    /// <summary>Username không tồn tại (hoặc đã bị khóa) trong bảng AppUsers.</summary>
    UserNotFound,

    /// <summary>Đã là thành viên rồi — không thêm trùng.</summary>
    AlreadyMember,

    /// <summary>Chủ project không cần (và không được) thêm chính mình làm thành viên.</summary>
    IsOwner,

    /// <summary>Người gọi không phải chủ project và không có quyền xem-tất-cả.</summary>
    NotAllowed
}

/// <summary>
/// Mời một người dùng vào project làm reviewer/stakeholder. Chỉ chủ project (CreatedByUsername) hoặc
/// người có quyền ProjectsViewAll (TeamDev/Admin) được mời. Username phải là tài khoản đang hoạt động —
/// lưu đúng chữ hoa/thường như trong AppUsers để so khớp ổn định ở mọi provider DB.
/// </summary>
public class AddProjectMemberUseCase
{
    private readonly AppDbContext _db;

    public AddProjectMemberUseCase(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AddProjectMemberResult> ExecuteAsync(
        Guid projectId, string? username, string? actorUsername, bool canManageAll, CancellationToken cancellationToken = default)
    {
        username = username?.Trim();
        if (string.IsNullOrEmpty(username))
            return AddProjectMemberResult.UserNotFound;

        var project = await _db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new { p.CreatedByUsername })
            .FirstOrDefaultAsync(cancellationToken);

        if (project == null)
            return AddProjectMemberResult.ProjectNotFound;

        if (!canManageAll && (project.CreatedByUsername == null || project.CreatedByUsername != actorUsername))
            return AddProjectMemberResult.NotAllowed;

        // So khớp bằng đẳng thức như mọi truy vấn username khác trong app (theo collation của DB);
        // lưu lại ĐÚNG chuỗi Username trong AppUsers để các phép lọc về sau so khớp ổn định.
        var user = await _db.AppUsers
            .Where(u => u.IsActive && u.Username == username)
            .Select(u => new { u.Username })
            .FirstOrDefaultAsync(cancellationToken);

        if (user == null)
            return AddProjectMemberResult.UserNotFound;

        if (project.CreatedByUsername == user.Username)
            return AddProjectMemberResult.IsOwner;

        var alreadyMember = await _db.ProjectMembers
            .AnyAsync(m => m.ProjectId == projectId && m.Username == user.Username, cancellationToken);

        if (alreadyMember)
            return AddProjectMemberResult.AlreadyMember;

        _db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = projectId,
            Username = user.Username,
            AddedByUsername = actorUsername
        });

        await _db.SaveChangesAsync(cancellationToken);
        return AddProjectMemberResult.Added;
    }
}
