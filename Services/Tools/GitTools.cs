using System.ComponentModel;
using System.Text.RegularExpressions;
using ICOGenerator.Services.Tools.PullRequests;

namespace ICOGenerator.Services.Tools;

/// <summary>
/// Các thao tác git của agent. MỌI method nhận <c>repoPath</c> — đường dẫn TƯƠNG ĐỐI tới thư mục gốc
/// của một repo trong workspace (xem <see cref="Artifacts.ProjectRepositoryLayout"/>).
/// <para>
/// Vì sao <c>repoPath</c> là tham số BẮT BUỘC chứ không có mặc định: trước đây mọi lệnh git chạy ở GỐC
/// workspace, mà gốc workspace không bao giờ là một git repo (các repo nằm ở
/// <c>04_Implementation/src/…</c>). Nghĩa là cả bước Pull Request chỉ nhận về
/// <c>fatal: not a git repository</c> — hoặc tệ hơn, nếu <c>AgentWorkspace:RootPath</c> vô tình nằm
/// trong một repo khác thì agent commit vào NHẦM repo. Một mặc định "gốc workspace" chỉ làm lỗi đó
/// quay lại lặng lẽ, nên tham số này không có giá trị mặc định: thiếu nó thì
/// <see cref="Registry.ToolArgumentValidator"/> từ chối lời gọi và bắt model gọi lại cho đúng.
/// </para>
/// </summary>
public class GitTools
{
    private readonly CommandTools _commandTools;
    private readonly IConfiguration _configuration;
    private readonly IPullRequestPublisher _pullRequestPublisher;
    public GitTools(CommandTools commandTools, IConfiguration configuration, IPullRequestPublisher pullRequestPublisher)
    { _commandTools = commandTools; _configuration = configuration; _pullRequestPublisher = pullRequestPublisher; }

    // All git operations go through CommandTools.RunArgs (no shell), so literal args may contain spaces/shell operators; the allowlist still applies to the "git <subcommand>" prefix.
    // SECURITY: LLM-controlled branch/remote names become positional git args, and git treats a '-'-prefixed arg as an OPTION ("argument injection", e.g. --upload-pack=…). IsSafeRef rejects anything but a plain ref token (must start alphanumeric, no leading dash, no spaces/shell-meta), closing that gap.
    private static readonly Regex SafeRef = new(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.Compiled);

    private static bool IsSafeRef(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Contains("..") && SafeRef.IsMatch(value);

    private static string Blocked(string what, string value) =>
        $"Command blocked for security reason (invalid git {what}: \"{value}\"). Allowed: letters, digits, '.', '_', '-', '/'; must not start with '-' or contain '..'.";

    /// <summary>
    /// Kiểm tra <paramref name="repoPath"/> trước khi chạy bất kỳ lệnh git nào: nằm trong workspace
    /// (chốt chặn dùng chung với các tool file) và thư mục đó thật sự là gốc một git repo.
    /// Trả về null nếu hợp lệ, ngược lại là thông điệp lỗi để trả thẳng cho model.
    /// <para>
    /// Đòi <c>.git</c> nằm ĐÚNG tại <paramref name="repoPath"/> là chặt hơn git (git chạy được từ thư
    /// mục con). Cố ý: các thao tác ở đây — commit tất cả, tạo nhánh, push — luôn tác động lên CẢ repo,
    /// nên nhận một thư mục con làm "repo" chỉ khiến bàn giao ghi sai chỗ mà không ai thấy.
    /// </para>
    /// </summary>
    private string? ValidateRepo(string repoPath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(repoPath))
            return "Missing repoPath: pass the repository folder relative to the workspace (see the repository list in the task prompt).";

        if (!_commandTools.TryResolveWorkingDirectory(repoPath, out fullPath, out var error))
            return error;

        // ".git" là thư mục ở repo thường, nhưng là FILE ở worktree/submodule — chấp nhận cả hai.
        var gitPath = Path.Combine(fullPath, ".git");
        if (!Directory.Exists(gitPath) && !File.Exists(gitPath))
            return $"'{repoPath}' is not a git repository root (no .git found there). Use one of the repository paths listed in the task prompt.";

        return null;
    }

    [Description("Show git status of one repository. repoPath is the repository folder relative to the workspace (e.g. \"04_Implementation/src/backend\").")]
    public Task<string> GitStatus(string repoPath)
    {
        var invalid = ValidateRepo(repoPath, out _);
        return invalid != null ? Task.FromResult(invalid) : _commandTools.RunArgs(["git", "status"], repoPath);
    }

    [Description("Show a git diff stat (summary of changed files) for one repository. repoPath is the repository folder relative to the workspace (e.g. \"04_Implementation/src/backend\").")]
    public Task<string> GitDiff(string repoPath)
    {
        var invalid = ValidateRepo(repoPath, out _);
        return invalid != null ? Task.FromResult(invalid) : _commandTools.RunArgs(["git", "diff", "--stat"], repoPath);
    }

    [Description("Create and checkout a new git branch in one repository. repoPath is the repository folder relative to the workspace (e.g. \"04_Implementation/src/backend\").")]
    public async Task<string> CreateBranch(string repoPath, string branchName, string baseBranch)
    {
        var invalid = ValidateRepo(repoPath, out _);
        if (invalid != null) return invalid;
        if (!IsSafeRef(baseBranch)) return Blocked("base branch", baseBranch);
        if (!IsSafeRef(branchName)) return Blocked("branch name", branchName);

        var fetchStatus = await _commandTools.RunArgs(["git", "status"], repoPath);
        // No "--": `git checkout -- <x>` treats <x> as a PATHSPEC not a branch, so baseBranch would be silently ignored. Flag-injection is already blocked by IsSafeRef, so "--" is unnecessary.
        var checkoutBase = await _commandTools.RunArgs(["git", "checkout", baseBranch], repoPath);
        var createBranch = await _commandTools.RunArgs(["git", "checkout", "-b", branchName], repoPath);
        return $"Repo: {repoPath}\n\nGit status:\n{fetchStatus}\n\nCheckout base:\n{checkoutBase}\n\nCreate branch:\n{createBranch}";
    }

    [Description("Commit generated code to git in one repository. repoPath is the repository folder relative to the workspace (e.g. \"04_Implementation/src/backend\").")]
    public async Task<string> GitCommit(string repoPath, string message)
    {
        var invalid = ValidateRepo(repoPath, out _);
        if (invalid != null) return invalid;

        var add = await _commandTools.RunArgs(["git", "add", "."], repoPath);
        // Message is a literal arg after "-m" (no shell), so git takes it as the value, not an option — may safely contain spaces, $, &, ; (these previously got the command blocked).
        var commit = await _commandTools.RunArgs(["git", "commit", "-m", message], repoPath);
        return $"Repo: {repoPath}\n\nGit add:\n{add}\n\nGit commit:\n{commit}";
    }

    [Description("Push a branch of one repository to its remote without merging. repoPath is the repository folder relative to the workspace (e.g. \"04_Implementation/src/backend\").")]
    public async Task<string> PushBranch(string repoPath, string branchName)
    {
        var invalid = ValidateRepo(repoPath, out _);
        if (invalid != null) return invalid;
        if (!IsSafeRef(branchName)) return Blocked("branch name", branchName);

        var remoteName = _configuration["PullRequest:RemoteName"] ?? "origin";
        if (!IsSafeRef(remoteName)) return Blocked("remote name", remoteName);

        return await _commandTools.RunArgs(["git", "push", "-u", remoteName, branchName], repoPath);
    }

    [Description("Push the committed feature branch of ONE repository and open a Pull Request for it. repoPath is the repository folder relative to the workspace (e.g. \"04_Implementation/src/backend\") — call this once per repository you changed. When a GitHub token is configured and the remote is GitHub, this CREATES the PR via API and returns its URL; otherwise it returns a ready-to-open PR/Merge Request link for the repo's host (GitHub, GitLab, Azure DevOps, Bitbucket). Call this AFTER committing the implemented code on a feature branch. Pass the repository path, the feature branch name, a concise PR title, and a short description body.")]
    public async Task<string> OpenPullRequest(string repoPath, string branchName, string title, string body)
    {
        var invalid = ValidateRepo(repoPath, out _);
        if (invalid != null) return invalid;
        if (!IsSafeRef(branchName)) return Blocked("branch name", branchName);

        var remoteName = _configuration["PullRequest:RemoteName"] ?? "origin";
        if (!IsSafeRef(remoteName)) return Blocked("remote name", remoteName);

        // Nhánh đích của PR; cấu hình PullRequest:BaseBranch, mặc định "main".
        var baseBranch = _configuration["PullRequest:BaseBranch"];
        if (string.IsNullOrWhiteSpace(baseBranch)) baseBranch = "main";
        if (!IsSafeRef(baseBranch)) return Blocked("base branch", baseBranch);

        var push = await _commandTools.RunArgs(["git", "push", "-u", remoteName, branchName], repoPath);

        // Remote URL thật của repo — do ProjectRepositorySeeder trỏ về repo CỦA DỰ ÁN
        // (Project.BackendGitUrl/FrontendGitUrl), không phải repo template đã clone lấy skeleton.
        // Dùng để gọi API GitHub tạo PR thật, hoặc suy ra link compare khi không tạo được.
        var remoteRaw = await _commandTools.RunArgs(["git", "remote", "get-url", remoteName], repoPath);
        var remoteUrl = ExtractStdout(remoteRaw);

        // Ưu tiên TẠO PR THẬT (GitHub + có token); không được thì xuống cấp êm về link "mở PR thủ công".
        var published = await _pullRequestPublisher.PublishAsync(remoteUrl, baseBranch, branchName, title, body);
        string resultLine;
        if (published.Created && published.Url is not null)
        {
            resultLine = $"✅ Đã tạo Pull Request: {published.Url}";
        }
        else
        {
            var prUrl = PullRequestUrlBuilder.Build(remoteUrl, baseBranch, branchName, title);
            resultLine = prUrl is null
                ? $"Chưa tạo được PR tự động ({published.Detail}) và không suy ra được link — hãy mở Pull Request thủ công trên trang repo."
                : $"Chưa tạo PR tự động ({published.Detail}). Mở Pull Request tại: {prUrl}";
        }

        return $"""
                Repo: {repoPath}
                Remote: {(string.IsNullOrWhiteSpace(remoteUrl) ? "(không đọc được remote — repo chưa được gắn remote?)" : remoteUrl)}

                Push branch:
                {push}

                PR title: {title}
                PR body:
                {body}

                Nhánh: {baseBranch} (base) ← {branchName} (head)
                {resultLine}
                """;
    }

    // Lấy phần stdout từ chuỗi kết quả đã định dạng của CommandTools (format do ta tự kiểm soát:
    // "Command/ExitCode/Output:/Error:"), để đọc ví dụ remote URL. Không khớp được thì trả rỗng →
    // PullRequestUrlBuilder trả null → caller báo mở PR thủ công (xuống cấp êm, không ném lỗi).
    private static string ExtractStdout(string decorated)
    {
        const string outMarker = "Output:\n";
        const string errMarker = "\n\nError:";
        var start = decorated.IndexOf(outMarker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += outMarker.Length;
        var end = decorated.IndexOf(errMarker, start, StringComparison.Ordinal);
        if (end < 0) end = decorated.Length;
        return decorated[start..end].Trim();
    }
}
