namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Đổ NỘI DUNG bộ khung chuẩn Bosch (cấu hình ở section <c>BoschTemplate</c>) vào các repo đích của
/// project để làm skeleton cho bước Implementation, khi project chọn "Use Bosch Template".
/// Backend → <c>04_Implementation/src/backend</c>; frontend → <c>04_Implementation/src/frontend</c>.
///
/// <para>
/// <b>Chép nội dung, KHÔNG clone thẳng vào chỗ đó.</b> Template được clone ra một thư mục tạm rồi chép
/// file sang (bỏ <c>.git</c>). Lý do: thư mục đích đã là repo CỦA DỰ ÁN do
/// <see cref="ProjectRepositorySeeder"/> dựng, và nó phải giữ nguyên lịch sử + remote của repo đích.
/// Clone thẳng template vào đó sẽ biến repo template thành repo làm việc — nghĩa là bước Pull Request
/// đẩy nhánh feature của khách hàng lên chính repo khung chuẩn. Chép nội dung thì skeleton vào PR như
/// một commit bình thường, đúng thứ người review muốn thấy.
/// </para>
///
/// Idempotent: thư mục đích đã có file (ngoài <c>.git</c>) thì bỏ qua — re-run/retry không ghi đè code
/// agent đã sửa, và repo dự án vốn đã có code thì không bị đổ skeleton lên trên.
/// URL/branch lấy từ cấu hình (admin), KHÔNG phải từ LLM; clone chạy qua <see cref="GitCli"/>
/// (ArgumentList, không qua shell) nên không có nguy cơ shell-injection.
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
    /// Đổ skeleton backend + frontend cho project (theo <paramref name="projectKey"/> — folder key
    /// duy nhất, xem <see cref="WorkspacePathResolver.GetWorkspaceFolder"/>). Trả về tóm tắt dạng đọc được.
    /// Ném ngoại lệ nếu một lệnh clone đã cấu hình bị fail, để worker đánh dấu task thất bại rõ ràng
    /// thay vì âm thầm cho Developer code vào skeleton rỗng.
    /// </summary>
    public async Task<string> SeedAsync(string projectKey, CancellationToken cancellationToken)
    {
        var srcPath = _workspacePathResolver.GetImplementationSourcePath(projectKey);
        Directory.CreateDirectory(srcPath);

        var branch = _configuration["BoschTemplate:Branch"];

        var backend = await CopyIfConfiguredAsync(
            "BoschTemplate:BackendRepoUrl", Path.Combine(srcPath, BackendFolderName), branch, "backend", cancellationToken);
        var frontend = await CopyIfConfiguredAsync(
            "BoschTemplate:FrontendRepoUrl", Path.Combine(srcPath, FrontendFolderName), branch, "frontend", cancellationToken);

        return $"{backend} | {frontend}";
    }

    private async Task<string> CopyIfConfiguredAsync(
        string urlKey, string targetDir, string? branch, string label, CancellationToken cancellationToken)
    {
        var url = _configuration[urlKey];
        if (string.IsNullOrWhiteSpace(url))
            return $"{label}: chưa cấu hình repo template ({urlKey}) — bỏ qua";

        if (HasContent(targetDir))
            return $"{label}: skeleton đã có sẵn — bỏ qua";

        Directory.CreateDirectory(targetDir);

        var tempDir = Path.Combine(Path.GetTempPath(), $"icogen-skeleton-{Guid.NewGuid():N}");
        try
        {
            var args = new List<string> { "clone", "--depth", "1" };
            // branch lấy từ config; vẫn chặn dạng "-…" để không bị hiểu nhầm thành option của git.
            if (!string.IsNullOrWhiteSpace(branch) && !branch.StartsWith('-'))
            {
                args.Add("--branch");
                args.Add(branch);
            }
            args.Add(url);
            args.Add(tempDir);

            var (exitCode, output) = await GitCli.RunAsync(args, workingDirectory: null, cancellationToken);
            if (exitCode != 0)
                throw new InvalidOperationException($"git clone skeleton Bosch ({label}) thất bại (exit {exitCode}): {output}");

            CopyDirectoryExceptGit(tempDir, targetDir);
            return $"{label}: đã chép skeleton từ template";
        }
        finally
        {
            TryDelete(tempDir);
        }
    }

    // "Có nội dung" = có bất kỳ thứ gì NGOÀI .git: thư mục đích thường đã là một repo trống vừa được
    // ProjectRepositorySeeder dựng, và một repo trống thì vẫn phải được đổ skeleton vào.
    private static bool HasContent(string dir) =>
        Directory.Exists(dir)
        && Directory.EnumerateFileSystemEntries(dir)
            .Any(entry => !string.Equals(Path.GetFileName(entry), ".git", StringComparison.Ordinal));

    // Chép cây thư mục, bỏ mọi thư mục .git của template: lịch sử cần giữ là của repo ĐÍCH.
    private static void CopyDirectoryExceptGit(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, ".git", StringComparison.Ordinal))
                continue;

            CopyDirectoryExceptGit(directory, Path.Combine(targetDir, name));
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                // Git đặt cờ read-only trên các file trong .git (packfile) ⇒ Delete có thể ném trên
                // Windows. Dọn thư mục tạm là việc phụ, không được làm hỏng một lượt seed đã thành công.
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }
}
