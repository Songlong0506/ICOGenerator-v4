using System.ComponentModel;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Tools;

public class GitTools
{
    private readonly CommandTools _commandTools;
    private readonly IConfiguration _configuration;
    public GitTools(CommandTools commandTools, IConfiguration configuration)
    { _commandTools = commandTools; _configuration = configuration; }

    // All git operations go through CommandTools.RunArgs (no shell): branch names and commit
    // messages are passed as literal arguments, so values containing spaces or characters the
    // shell would treat as operators ($, &, ;, …) are no longer rejected by the shell-operator
    // guard. The command allowlist still applies to the "git <subcommand>" prefix.
    //
    // Branch/remote names are LLM-controlled and become POSITIONAL git arguments. Even without a
    // shell, git itself treats an argument starting with '-' as an OPTION (git "argument
    // injection"), so a value like "--upload-pack=…" or "--output=…" could change what git does.
    // ValidateRef rejects anything that isn't a plain ref token (must start alphanumeric, no
    // leading dash, no spaces/shell-meta), closing that gap.
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
        // Switch to the base branch WITHOUT "--": `git checkout -- <x>` makes git treat <x> as a
        // PATHSPEC (a file to restore from the index), not a branch — so the base branch would
        // never actually be checked out and the new branch below would fork from whatever HEAD
        // already was, silently ignoring baseBranch. Flag-injection is already prevented by
        // IsSafeRef (rejects a leading '-' and '..'), so the disambiguating "--" is unnecessary here.
        var checkoutBase = await _commandTools.RunArgs(["git", "checkout", baseBranch]);
        var createBranch = await _commandTools.RunArgs(["git", "checkout", "-b", branchName]);
        return $"Git status:\n{fetchStatus}\n\nCheckout base:\n{checkoutBase}\n\nCreate branch:\n{createBranch}";
    }

    [Description("Commit generated code to git.")]
    public async Task<string> GitCommit(string message)
    {
        var add = await _commandTools.RunArgs(["git", "add", "."]);
        // The message is a literal argument passed after "-m" (no shell), so git consumes it as
        // the message value, not an option — it needs no quote mangling and may safely contain
        // spaces, $, &, ; etc. (these previously got the whole command blocked).
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
