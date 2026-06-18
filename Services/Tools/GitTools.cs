using System.ComponentModel;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Tools;

public class GitTools
{
    private readonly CommandTools _commandTools;
    private readonly IConfiguration _configuration;
    public GitTools(CommandTools commandTools, IConfiguration configuration)
    { _commandTools = commandTools; _configuration = configuration; }

    // All git operations go through CommandTools.RunArgs (no shell), so literal args may contain spaces/shell operators; the allowlist still applies to the "git <subcommand>" prefix.
    // SECURITY: LLM-controlled branch/remote names become positional git args, and git treats a '-'-prefixed arg as an OPTION ("argument injection", e.g. --upload-pack=…). IsSafeRef rejects anything but a plain ref token (must start alphanumeric, no leading dash, no spaces/shell-meta), closing that gap.
    private static readonly Regex SafeRef = new(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.Compiled);

    private static bool IsSafeRef(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Contains("..") && SafeRef.IsMatch(value);

    private static string Blocked(string what, string value) =>
        $"Command blocked for security reason (invalid git {what}: \"{value}\"). Allowed: letters, digits, '.', '_', '-', '/'; must not start with '-' or contain '..'.";

    [Description("Show git status.")]
    public Task<string> GitStatus() => _commandTools.RunArgs(["git", "status"]);

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
}
