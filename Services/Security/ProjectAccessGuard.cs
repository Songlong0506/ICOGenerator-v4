using System.Security.Claims;
using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Security;

/// <summary>
/// Hiện thực <see cref="IProjectAccessGuard"/>: ProjectsViewAll ⇒ pass ngay (không chạm DB —
/// PermissionService đã cache theo role); ngược lại một query AnyAsync đối chiếu
/// Project.CreatedByUsername với username hiện tại (đi qua index (CreatedByUsername, CreatedAt)
/// hoặc PK tùy đường). Các overload theo id tài nguyên (document/revision/source/call log) giải
/// ngược về project trong CÙNG một query để "không tồn tại" và "không phải của bạn" trả về giống
/// hệt nhau — caller không phân biệt được hai trường hợp, khỏi rò rỉ sự tồn tại của tài nguyên.
/// </summary>
public class ProjectAccessGuard : IProjectAccessGuard
{
    private readonly AppDbContext _db;
    private readonly IPermissionService _permissions;

    public ProjectAccessGuard(AppDbContext db, IPermissionService permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    public Task<bool> CanAccessProjectAsync(ClaimsPrincipal user, Guid projectId, CancellationToken cancellationToken = default) =>
        CheckAsync(user,
            username => _db.Projects.AsNoTracking()
                .AnyAsync(p => p.Id == projectId && p.CreatedByUsername == username, cancellationToken),
            cancellationToken);

    public Task<bool> CanAccessDocumentAsync(ClaimsPrincipal user, Guid documentId, CancellationToken cancellationToken = default) =>
        CheckAsync(user,
            username => _db.ProjectDocuments.AsNoTracking()
                .AnyAsync(d => d.Id == documentId && d.Project.CreatedByUsername == username, cancellationToken),
            cancellationToken);

    public Task<bool> CanAccessDocumentRevisionAsync(ClaimsPrincipal user, Guid revisionId, CancellationToken cancellationToken = default) =>
        CheckAsync(user,
            username => _db.ProjectDocumentRevisions.AsNoTracking()
                .AnyAsync(r => r.Id == revisionId && r.ProjectDocument.Project.CreatedByUsername == username, cancellationToken),
            cancellationToken);

    public Task<bool> CanAccessSourceFileAsync(ClaimsPrincipal user, Guid sourceFileId, CancellationToken cancellationToken = default) =>
        CheckAsync(user,
            username => _db.ProjectSourceFiles.AsNoTracking()
                .AnyAsync(s => s.Id == sourceFileId && s.Project.CreatedByUsername == username, cancellationToken),
            cancellationToken);

    public Task<bool> CanAccessCallLogAsync(ClaimsPrincipal user, Guid callLogId, CancellationToken cancellationToken = default) =>
        CheckAsync(user,
            username => _db.AgentModelCallLogs.AsNoTracking()
                .AnyAsync(l => l.Id == callLogId && l.Project!.CreatedByUsername == username, cancellationToken),
            cancellationToken);

    // Lõi chung: ProjectsViewAll pass ngay; còn lại chạy query "tài nguyên thuộc project do username
    // tạo" do từng overload cung cấp. Username trống (không đăng nhập / claim hỏng) không bao giờ pass
    // — và cũng không khớp được CreatedByUsername null vì username truyền vào luôn non-null ở nhánh đó.
    private async Task<bool> CheckAsync(ClaimsPrincipal user, Func<string, Task<bool>> ownedByAsync, CancellationToken cancellationToken)
    {
        if (await _permissions.HasPermissionAsync(user, AppPermission.ProjectsViewAll, cancellationToken))
            return true;

        var username = user.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username))
            return false;

        return await ownedByAsync(username);
    }
}
