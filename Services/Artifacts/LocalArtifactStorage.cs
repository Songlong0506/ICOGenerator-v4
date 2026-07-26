
namespace ICOGenerator.Services.Artifacts;

public class LocalArtifactStorage : IArtifactStorage
{
    private readonly WorkspacePathResolver _workspacePathResolver;
    private readonly ILogger<LocalArtifactStorage> _logger;

    public LocalArtifactStorage(WorkspacePathResolver workspacePathResolver, ILogger<LocalArtifactStorage> logger)
    {
        _workspacePathResolver = workspacePathResolver;
        _logger = logger;
    }

    public void InitializeProjectWorkspace(string projectKey)
    {
        Directory.CreateDirectory(_workspacePathResolver.GetProjectWorkspacePath(projectKey));
        foreach (var phase in ProjectWorkspaceLayout.Phases)
            Directory.CreateDirectory(_workspacePathResolver.GetPhasePath(projectKey, phase));
    }

    public bool TryRenameProjectWorkspace(string oldProjectKey, string newProjectKey)
    {
        // Tên project khác nhau vẫn có thể cho ra CÙNG một key (vd "Task App" và "task-app" đều thành
        // "task-app-<id8>") — khi đó không có gì phải làm trên đĩa.
        if (string.Equals(oldProjectKey, newProjectKey, StringComparison.Ordinal))
            return true;

        string oldPath;
        string newPath;
        try
        {
            oldPath = _workspacePathResolver.GetProjectWorkspacePath(oldProjectKey);
            newPath = _workspacePathResolver.GetProjectWorkspacePath(newProjectKey);
        }
        catch (Exception ex)
        {
            // AgentWorkspace:RootPath thiếu/sai trên máy này ⇒ workspace chưa từng tạo được (xem
            // CreateProjectUseCase: khởi tạo thư mục cũng chỉ best-effort). Không có dữ liệu để mất nên
            // KHÔNG chặn việc đổi tên project.
            _logger.LogWarning(ex, "Could not resolve workspace paths to rename {OldKey} -> {NewKey}.", oldProjectKey, newProjectKey);
            return true;
        }

        // Project chưa chạy gì (chưa có tài liệu/POC) thì thư mục có thể chưa tồn tại — đổi tên xong,
        // lần ghi đầu tiên sẽ tự tạo thư mục theo tên mới.
        if (!Directory.Exists(oldPath))
            return true;

        // Key chứa 8 ký tự đầu của Id project nên đích chỉ có thể tồn tại nếu chính project này để lại
        // thư mục rác trùng tên. Gộp hai thư mục là việc rủi ro (ghi đè tài liệu) — dừng lại, để người
        // dùng biết và tự xử lý thay vì âm thầm mất dữ liệu.
        if (Directory.Exists(newPath))
        {
            _logger.LogWarning("Workspace folder {NewPath} already exists; refusing to merge from {OldPath}.", newPath, oldPath);
            return false;
        }

        try
        {
            Directory.Move(oldPath, newPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not rename workspace folder {OldPath} -> {NewPath}.", oldPath, newPath);
            return false;
        }
    }

    public string GetDraftPath(string projectKey, ProjectArtifactDescriptor artifact) =>
        Path.Combine(_workspacePathResolver.GetPhaseDraftPath(projectKey, artifact.Phase), artifact.FileName);

    public string GetVersionPath(string projectKey, string versionName, ProjectArtifactDescriptor artifact) =>
        Path.Combine(_workspacePathResolver.GetPhaseVersionPath(projectKey, artifact.Phase, versionName), artifact.FileName);

    public string GetSourceUploadDir(string projectKey) =>
        Path.Combine(_workspacePathResolver.GetProjectWorkspacePath(projectKey), "00_Source");
}
