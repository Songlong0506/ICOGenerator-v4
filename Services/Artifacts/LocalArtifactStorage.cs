
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

    public bool TryCopyProjectWorkspace(string sourceProjectKey, string targetProjectKey, IReadOnlyCollection<string>? onlyTopLevelFolders = null)
    {
        // Key chứa 8 ký tự đầu của Id project, mà bản sao luôn có Id mới ⇒ hai key không thể trùng nhau.
        // Nếu trùng thì caller đã truyền nhầm, và chép đè lên chính nó là việc phá dữ liệu — chặn ngay.
        if (string.Equals(sourceProjectKey, targetProjectKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("Refusing to copy workspace {Key} onto itself.", sourceProjectKey);
            return false;
        }

        string sourcePath;
        string targetPath;
        try
        {
            sourcePath = _workspacePathResolver.GetProjectWorkspacePath(sourceProjectKey);
            targetPath = _workspacePathResolver.GetProjectWorkspacePath(targetProjectKey);
        }
        catch (Exception ex)
        {
            // RootPath thiếu/sai trên máy này ⇒ workspace chưa từng tạo được, không có dữ liệu để mất.
            // Cùng lý lẽ với TryRenameProjectWorkspace: không chặn thao tác vì một thứ best-effort.
            _logger.LogWarning(ex, "Could not resolve workspace paths to copy {SourceKey} -> {TargetKey}.", sourceProjectKey, targetProjectKey);
            return true;
        }

        // Project nguồn chưa chạy gì (chưa có tài liệu/POC) thì thư mục có thể chưa tồn tại — không có gì
        // để chép, lần ghi đầu tiên của bản sao sẽ tự tạo thư mục.
        if (!Directory.Exists(sourcePath))
            return true;

        // Đích chỉ có thể tồn tại nếu một lần nhân bản trước để lại thư mục rác trùng tên. Gộp hai thư mục
        // là việc rủi ro (ghi đè tài liệu) — dừng lại thay vì âm thầm trộn dữ liệu hai dự án.
        if (Directory.Exists(targetPath))
        {
            _logger.LogWarning("Workspace folder {TargetPath} already exists; refusing to copy from {SourcePath}.", targetPath, sourcePath);
            return false;
        }

        try
        {
            CopyDirectory(sourcePath, targetPath, onlyTopLevelFolders);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy workspace folder {SourcePath} -> {TargetPath}.", sourcePath, targetPath);
            // Bản chép dở dang còn tệ hơn không chép gì: nó chiếm chỗ thư mục đích và làm lần nhân bản sau
            // bị từ chối ở nhánh "đích đã tồn tại" ngay trên.
            TryDeleteDirectory(targetPath);
            return false;
        }
    }

    public void TryDeleteProjectWorkspace(string projectKey)
    {
        try
        {
            TryDeleteDirectory(_workspacePathResolver.GetProjectWorkspacePath(projectKey));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve workspace path to delete {ProjectKey}.", projectKey);
        }
    }

    private static void CopyDirectory(string sourcePath, string targetPath, IReadOnlyCollection<string>? onlyTopLevelFolders)
    {
        Directory.CreateDirectory(targetPath);

        // File nằm thẳng ở gốc workspace không thuộc thư mục cấp 1 nào; bản sao "chỉ phần yêu cầu" cố ý
        // không lấy chúng (chúng là sản phẩm sinh ra, không phải đầu vào).
        if (onlyTopLevelFolders == null)
        {
            foreach (var file in Directory.EnumerateFiles(sourcePath))
                File.Copy(file, Path.Combine(targetPath, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.EnumerateDirectories(sourcePath))
        {
            var name = Path.GetFileName(dir);
            if (onlyTopLevelFolders != null && !onlyTopLevelFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            CopyTree(dir, Path.Combine(targetPath, name));
        }
    }

    private static void CopyTree(string sourceDir, string targetDir)
    {
        // node_modules/bin/obj/.git/.vs sinh lại được và chiếm phần lớn dung lượng của một workspace đã
        // build — WorkspaceFileFilter là nguồn chân lý duy nhất của danh sách đó.
        if (WorkspaceFileFilter.RegenerableDirectories.Contains(Path.GetFileName(sourceDir)))
            return;

        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)));

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            CopyTree(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete workspace folder {Path}.", path);
        }
    }

    public string GetDraftPath(string projectKey, ProjectArtifactDescriptor artifact) =>
        Path.Combine(_workspacePathResolver.GetPhaseDraftPath(projectKey, artifact.Phase), artifact.FileName);

    public string GetVersionPath(string projectKey, string versionName, ProjectArtifactDescriptor artifact) =>
        Path.Combine(_workspacePathResolver.GetPhaseVersionPath(projectKey, artifact.Phase, versionName), artifact.FileName);

    public string GetSourceUploadDir(string projectKey) =>
        Path.Combine(_workspacePathResolver.GetProjectWorkspacePath(projectKey), "00_Source");
}
