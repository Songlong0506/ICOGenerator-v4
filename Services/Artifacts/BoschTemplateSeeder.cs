using System.Diagnostics;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Clone bộ khung chuẩn Bosch (cấu hình ở section <c>BoschTemplate</c>) vào workspace của project
/// để làm skeleton cho bước Implementation, khi project chọn "Use Bosch Template".
/// Backend → <c>04_Implementation/src/backend</c>; frontend → <c>04_Implementation/src/frontend</c>.
///
/// Idempotent: nếu thư mục đích đã có file thì bỏ qua (re-run / retry không ghi đè code agent đã sửa).
/// URL/branch lấy từ cấu hình (admin), KHÔNG phải từ LLM; clone chạy qua <see cref="ProcessStartInfo"/>
/// với ArgumentList (không qua shell) nên không có nguy cơ shell-injection.
/// </summary>
public class BoschTemplateSeeder
{
    private readonly IConfiguration _configuration;
    private readonly WorkspacePathResolver _workspacePathResolver;

    public const string BackendFolderName = "backend";
    public const string FrontendFolderName = "frontend";

    public BoschTemplateSeeder(IConfiguration configuration, WorkspacePathResolver workspacePathResolver)
    {
        _configuration = configuration;
        _workspacePathResolver = workspacePathResolver;
    }

    /// <summary>
    /// Clone skeleton backend + frontend cho project (theo <paramref name="projectKey"/> — folder key
    /// duy nhất, xem <see cref="WorkspacePathResolver.GetWorkspaceFolder"/>). Trả về tóm tắt dạng đọc được.
    /// Ném ngoại lệ nếu một lệnh clone đã cấu hình bị fail, để worker đánh dấu task thất bại rõ ràng
    /// thay vì âm thầm cho Developer code vào skeleton rỗng.
    /// </summary>
    public async Task<string> SeedAsync(string projectKey, CancellationToken cancellationToken)
    {
        var srcPath = _workspacePathResolver.GetImplementationSourcePath(projectKey);
        Directory.CreateDirectory(srcPath);

        var branch = _configuration["BoschTemplate:Branch"];

        var backend = await CloneIfConfiguredAsync(
            "BoschTemplate:BackendRepoUrl", Path.Combine(srcPath, BackendFolderName), branch, "backend", cancellationToken);
        var frontend = await CloneIfConfiguredAsync(
            "BoschTemplate:FrontendRepoUrl", Path.Combine(srcPath, FrontendFolderName), branch, "frontend", cancellationToken);

        return $"{backend} | {frontend}";
    }

    private async Task<string> CloneIfConfiguredAsync(
        string urlKey, string targetDir, string? branch, string label, CancellationToken cancellationToken)
    {
        var url = _configuration[urlKey];
        if (string.IsNullOrWhiteSpace(url))
            return $"{label}: chưa cấu hình repo template ({urlKey}) — bỏ qua";

        if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
            return $"{label}: skeleton đã có sẵn — bỏ qua";

        Directory.CreateDirectory(targetDir);

        var args = new List<string> { "clone", "--depth", "1" };
        // branch lấy từ config; vẫn chặn dạng "-…" để không bị hiểu nhầm thành option của git.
        if (!string.IsNullOrWhiteSpace(branch) && !branch.StartsWith('-'))
        {
            args.Add("--branch");
            args.Add(branch);
        }
        args.Add(url);
        args.Add(targetDir);

        var (exitCode, output) = await RunGitAsync(args, cancellationToken);
        if (exitCode != 0)
            throw new InvalidOperationException($"git clone skeleton Bosch ({label}) thất bại (exit {exitCode}): {output}");

        return $"{label}: đã clone từ template";
    }

    private static async Task<(int ExitCode, string Output)> RunGitAsync(
        IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Không khởi chạy được 'git'. Máy chủ đã cài git chưa?");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
    }
}
