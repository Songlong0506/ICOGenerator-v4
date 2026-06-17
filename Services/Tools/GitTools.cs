using System.ComponentModel;

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

    [Description("Show git status.")]
    public Task<string> GitStatus() => _commandTools.RunArgs(["git", "status"]);

    [Description("Create and checkout a new git branch.")]
    public async Task<string> CreateBranch(string branchName, string baseBranch)
    {
        var fetchStatus = await _commandTools.RunArgs(["git", "status"]);
        var checkoutBase = await _commandTools.RunArgs(["git", "checkout", baseBranch]);
        var createBranch = await _commandTools.RunArgs(["git", "checkout", "-b", branchName]);
        return $"Git status:\n{fetchStatus}\n\nCheckout base:\n{checkoutBase}\n\nCreate branch:\n{createBranch}";
    }

    [Description("Commit generated code to git.")]
    public async Task<string> GitCommit(string message)
    {
        var add = await _commandTools.RunArgs(["git", "add", "."]);
        // The message is a literal argument (no shell), so it needs no quote mangling and may
        // safely contain spaces, $, &, ; etc. — these previously got the whole command blocked.
        var commit = await _commandTools.RunArgs(["git", "commit", "-m", message]);
        return $"Git add:\n{add}\n\nGit commit:\n{commit}";
    }

    [Description("Push branch to remote without merging.")]
    public Task<string> PushBranch(string branchName)
    {
        var remoteName = _configuration["PullRequest:RemoteName"] ?? "origin";
        return _commandTools.RunArgs(["git", "push", "-u", remoteName, branchName]);
    }
}
