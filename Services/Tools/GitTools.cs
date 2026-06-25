using System.ComponentModel;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Tools;

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

    [Description("Show git status.")]
    public Task<string> GitStatus() => _commandTools.RunArgs(["git", "status"]);

    [Description("Show a git diff stat (summary of changed files) for the current workspace.")]
    public Task<string> GitDiff() => _commandTools.RunArgs(["git", "diff", "--stat"]);

    [Description("Create and checkout a new git branch.")]
    public async Task<string> CreateBranch(string branchName, string baseBranch)
    {
        if (!IsSafeRef(baseBranch)) return Blocked("base branch", baseBranch);
        if (!IsSafeRef(branchName)) return Blocked("branch name", branchName);

        var fetchStatus = await _commandTools.RunArgs(["git", "status"]);
        // No "--": `git checkout -- <x>` treats <x> as a PATHSPEC not a branch, so baseBranch would be silently ignored. Flag-injection is already blocked by IsSafeRef, so "--" is unnecessary.
        var checkoutBase = await _commandTools.RunArgs(["git", "checkout", baseBranch]);
        var createBranch = await _commandTools.RunArgs(["git", "checkout", "-b", branchName]);
        return $"Git status:\n{fetchStatus}\n\nCheckout base:\n{checkoutBase}\n\nCreate branch:\n{createBranch}";
    }

    [Description("Commit generated code to git.")]
    public async Task<string> GitCommit(string message)
    {
        var add = await _commandTools.RunArgs(["git", "add", "."]);
        // Message is a literal arg after "-m" (no shell), so git takes it as the value, not an option — may safely contain spaces, $, &, ; (these previously got the command blocked).
        var commit = await _commandTools.RunArgs(["git", "commit", "-m", message]);
        return $"Git add:\n{add}\n\nGit commit:\n{commit}";
    }

    [Description("Push branch to remote without merging.")]
    public Task<string> PushBranch(string branchName)
    {
        if (!IsSafeRef(branchName)) return Task.FromResult(Blocked("branch name", branchName));

        var remoteName = _configuration["PullRequest:RemoteName"] ?? "origin";
        if (!IsSafeRef(remoteName)) return Task.FromResult(Blocked("remote name", remoteName));

        return _commandTools.RunArgs(["git", "push", "-u", remoteName, branchName]);
    }

    [Description("Push the committed feature branch and open a Pull Request. When a GitHub token is configured and the remote is GitHub, this CREATES the PR via API and returns its URL; otherwise it returns a ready-to-open PR/Merge Request link for the repo's host (GitHub, GitLab, Azure DevOps, Bitbucket). Call this AFTER committing the implemented code on a feature branch. Pass the feature branch name, a concise PR title, and a short description body.")]
    public async Task<string> OpenPullRequest(string branchName, string title, string body)
    {
        if (!IsSafeRef(branchName)) return Blocked("branch name", branchName);

        var remoteName = _configuration["PullRequest:RemoteName"] ?? "origin";
        if (!IsSafeRef(remoteName)) return Blocked("remote name", remoteName);

        // Nhánh đích của PR; cấu hình PullRequest:BaseBranch, mặc định "main".
        var baseBranch = _configuration["PullRequest:BaseBranch"];
        if (string.IsNullOrWhiteSpace(baseBranch)) baseBranch = "main";
        if (!IsSafeRef(baseBranch)) return Blocked("base branch", baseBranch);

        var push = await _commandTools.RunArgs(["git", "push", "-u", remoteName, branchName]);

        // Remote URL thật của repo (nguồn chân lý, kể cả Bosch template đã clone) — dùng để gọi API
        // GitHub tạo PR thật, hoặc suy ra link compare khi không tạo được.
        var remoteRaw = await _commandTools.RunArgs(["git", "remote", "get-url", remoteName]);
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
