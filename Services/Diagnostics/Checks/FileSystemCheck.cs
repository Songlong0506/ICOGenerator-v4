using ICOGenerator.Localization;

namespace ICOGenerator.Services.Diagnostics.Checks;

/// <summary>
/// Thư mục app phải GHI được (workspace của agent, Logs) và thư mục bản publish phải MANG THEO (Templates,
/// Prompts). Ghi thử một file tạm rồi xóa — quyền NTFS trên IIS cấp theo application pool, nhìn bằng Explorer
/// dưới tài khoản admin không nói được gì.
/// </summary>
public sealed class FileSystemCheck : IEnvironmentCheck
{
    private const long LowDiskBytes = 5L * 1024 * 1024 * 1024;

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public FileSystemCheck(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public string Group => Loc.T("EnvCheck.Files.Group");
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public Task<IReadOnlyList<EnvironmentCheckResult>> RunAsync(EnvironmentCheckContext context, CancellationToken cancellationToken)
    {
        var results = new List<EnvironmentCheckResult>();
        var account = Environment.UserName;

        // ---- workspace của agent
        var name = "AgentWorkspace:RootPath";
        var root = _configuration["AgentWorkspace:RootPath"]?.Trim() ?? string.Empty;
        if (root.Length == 0 || !Path.IsPathFullyQualified(root))
        {
            results.Add(new(Group, name, EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.Files.Workspace.NotAbsolute", root),
                Loc.T("EnvCheck.Files.Workspace.NotAbsolute.Fix")));
        }
        else
        {
            // Chưa có thì tự tạo (giống Logs) — app vốn cũng tạo thư mục này khi tạo project đầu tiên; chỉ Fail
            // khi tài khoản chạy app không có quyền tạo ở thư mục cha.
            try
            {
                Directory.CreateDirectory(root);
                results.Add(WriteProbe(name, root, account));
                results.Add(DiskSpace(root));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new(Group, name, EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.Files.Workspace.Missing", root),
                    Loc.T("EnvCheck.Files.Writable.Fix", account), ex.Message));
            }
        }

        // ---- Logs (Serilog ghi Logs/ tương đối với thư mục làm việc = content root khi chạy trong IIS)
        var logs = Path.Combine(_environment.ContentRootPath, "Logs");
        try
        {
            Directory.CreateDirectory(logs);
            results.Add(WriteProbe("Logs", logs, account));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            results.Add(new(Group, "Logs", EnvironmentCheckStatus.Warning, Loc.T("EnvCheck.Files.NotWritable", logs),
                Loc.T("EnvCheck.Files.Writable.Fix", account), ex.Message));
        }

        // ---- thư mục đi kèm bản publish
        foreach (var folder in new[] { "Templates", "Prompts" })
        {
            var path = Path.Combine(_environment.ContentRootPath, folder);
            var ok = Directory.Exists(path) && Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
            results.Add(ok
                ? new(Group, folder, EnvironmentCheckStatus.Pass, path)
                : new(Group, folder, EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.Files.Content.Missing", path),
                    Loc.T("EnvCheck.Files.Content.Fix")));
        }

        return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>(results);
    }

    private EnvironmentCheckResult WriteProbe(string name, string directory, string account)
    {
        var probe = Path.Combine(directory, $".ico-envcheck-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new(Group, name, EnvironmentCheckStatus.Pass, Loc.T("EnvCheck.Files.Writable", directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(Group, name, EnvironmentCheckStatus.Fail, Loc.T("EnvCheck.Files.NotWritable", directory),
                Loc.T("EnvCheck.Files.Writable.Fix", account), ex.Message);
        }
    }

    private EnvironmentCheckResult DiskSpace(string directory)
    {
        var name = Loc.T("EnvCheck.Files.Disk");
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(directory)!);
            var freeGb = drive.AvailableFreeSpace / 1024d / 1024 / 1024;
            return drive.AvailableFreeSpace < LowDiskBytes
                ? new(Group, name, EnvironmentCheckStatus.Warning, Loc.T("EnvCheck.Files.Disk.Low", drive.Name, freeGb.ToString("0.0")),
                    Loc.T("EnvCheck.Files.Disk.Fix"))
                : new(Group, name, EnvironmentCheckStatus.Pass, Loc.T("EnvCheck.Files.Disk.Ok", drive.Name, freeGb.ToString("0.0")));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new(Group, name, EnvironmentCheckStatus.Info, ex.Message);
        }
    }
}
