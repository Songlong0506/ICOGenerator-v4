using ICOGenerator.Data;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Projects;

public enum UpdateProjectResult
{
    Updated,
    /// <summary>Form gửi lên đúng y dữ liệu đang có — không ghi gì, không sinh audit log.</summary>
    NoChange,
    NameRequired,
    ProjectNotFound,
    /// <summary>Đổi TÊN bị chặn vì đang có workflow chạy (agent nền đang ghi vào thư mục workspace cũ).</summary>
    RenameBlockedByRunningWorkflow,
    /// <summary>Đổi tên thư mục workspace thất bại ⇒ hủy toàn bộ thay đổi để không bỏ rơi tài liệu đã sinh.</summary>
    WorkspaceRenameFailed
}

/// <summary>
/// Sửa thông tin cơ bản của một project (Name / Description / đơn vị yêu cầu) từ trang Projects.
///
/// Điểm cần cẩn thận: tên thư mục workspace trên đĩa DẪN XUẤT từ tên project
/// (<see cref="WorkspacePathResolver.GetWorkspaceFolder"/>), nên đổi tên mà không đổi thư mục sẽ làm mọi
/// đường dẫn tính lại trỏ sang một thư mục trống — POC/tài liệu đã sinh coi như mất. Vì vậy use case này
/// đổi tên thư mục TRƯỚC khi lưu DB; thất bại thì hủy (giữ tên cũ), và nếu lưu DB lỗi sau đó thì đổi
/// thư mục về tên cũ để hai bên không lệch nhau.
/// </summary>
public class UpdateProjectUseCase
{
    private readonly AppDbContext _db;
    private readonly IArtifactStorage _artifactStorage;
    private readonly IAuditLogger _audit;

    public UpdateProjectUseCase(AppDbContext db, IArtifactStorage artifactStorage, IAuditLogger audit)
    {
        _db = db;
        _artifactStorage = artifactStorage;
        _audit = audit;
    }

    public async Task<UpdateProjectResult> ExecuteAsync(ProjectEditVm vm, CancellationToken cancellationToken = default)
    {
        var name = (vm.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return UpdateProjectResult.NameRequired;

        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == vm.ProjectId, cancellationToken);
        if (project == null)
            return UpdateProjectResult.ProjectNotFound;

        var description = (vm.Description ?? string.Empty).Trim();
        var orgUnitCode = await ResolveOrgUnitCodeAsync(vm.OrgUnitCode, cancellationToken);

        var isRename = !string.Equals(project.Name, name, StringComparison.Ordinal);
        if (!isRename
            && string.Equals(project.Description, description, StringComparison.Ordinal)
            && string.Equals(project.OrgUnitCode, orgUnitCode, StringComparison.Ordinal))
            return UpdateProjectResult.NoChange;

        var oldName = project.Name;
        var oldKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, oldName);
        var newKey = WorkspacePathResolver.GetWorkspaceFolder(project.Id, name);

        if (isRename)
        {
            // Agent nền giải đường dẫn workspace MỘT LẦN lúc bắt đầu task (AgentTaskWorker /
            // AgentRunService) và ghi file suốt cả run — rút thư mục ra khỏi dưới chân nó sẽ làm run đang
            // chạy ghi vào một thư mục đã bị đổi tên. Chặn ở đây thay vì sửa nửa vời; Description và đơn
            // vị yêu cầu vẫn sửa được bình thường trong lúc workflow chạy.
            if (await HasRunningWorkflowAsync(project.Id, cancellationToken))
                return UpdateProjectResult.RenameBlockedByRunningWorkflow;

            if (!_artifactStorage.TryRenameProjectWorkspace(oldKey, newKey))
                return UpdateProjectResult.WorkspaceRenameFailed;
        }

        var before = Snapshot(project);
        project.Name = name;
        project.Description = description;
        project.OrgUnitCode = orgUnitCode;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // DB không lưu được sau khi thư mục đã đổi tên: trả thư mục về tên cũ (best-effort) để tên
            // project trong DB và tên thư mục trên đĩa không lệch nhau.
            if (isRename)
                _artifactStorage.TryRenameProjectWorkspace(newKey, oldKey);
            throw;
        }

        await _audit.LogAsync(AuditCategory.Project, AuditAction.Update, project.Id.ToString(),
            $"Cập nhật dự án \"{project.Name}\"",
            before: before, after: Snapshot(project), cancellationToken: cancellationToken);

        return UpdateProjectResult.Updated;
    }

    // Cùng luật với CreateProjectUseCase: chỉ nhận mã CÓ THẬT trong OrgUnits (dropdown render từ DB nên mã
    // lạ chỉ đến từ request tự chế) — mã lạ coi như "không chọn" thay vì chặn cả lần lưu vì một field phụ.
    private async Task<string?> ResolveOrgUnitCodeAsync(string? input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var code = input.Trim();
        return await _db.OrgUnits.AnyAsync(u => !u.IsDelete && u.OrgUnitCode == code, cancellationToken)
            ? code
            : null;
    }

    // "Đang chạy" hiểu theo đúng nghĩa trang Projects đang hiển thị (GetProjectListQuery): trạng thái của
    // run MỚI NHẤT chưa về đích (Completed/Failed/Canceled). Cổng duyệt (WaitingForHuman) cũng tính là
    // đang chạy — pipeline sẽ tiếp tục ghi vào đúng thư mục đó ngay khi được duyệt.
    private async Task<bool> HasRunningWorkflowAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var latestStatus = await _db.WorkflowRuns.AsNoTracking()
            .Where(w => w.ProjectId == projectId)
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => (WorkflowRunStatus?)w.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return latestStatus is not null
            && latestStatus is not WorkflowRunStatus.Completed
                and not WorkflowRunStatus.Failed
                and not WorkflowRunStatus.Canceled;
    }

    private static object Snapshot(Domain.Project project) => new
    {
        project.Name,
        project.Description,
        project.OrgUnitCode
    };
}
