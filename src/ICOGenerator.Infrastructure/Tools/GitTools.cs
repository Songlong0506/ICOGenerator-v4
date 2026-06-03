using System.ComponentModel;

namespace ICOGenerator.Services.Tools;

public class GitTools
{
    private readonly CommandTools _commandTools;
    private readonly IConfiguration _configuration;
    public GitTools(CommandTools commandTools, IConfiguration configuration)
    { _commandTools = commandTools; _configuration = configuration; }

    [Description("Show git status.")]
    public Task<string> GitStatus() => _commandTools.RunCommand("git status");

    [Description("Create and checkout a new git branch.")]
    public async Task<string> CreateBranch(string branchName, string baseBranch)
    {
        var fetchStatus = await _commandTools.RunCommand("git status");
        var checkoutBase = await _commandTools.RunCommand($"git checkout {baseBranch}");
        var createBranch = await _commandTools.RunCommand($"git checkout -b {branchName}");
        return $"Git status:\n{fetchStatus}\n\nCheckout base:\n{checkoutBase}\n\nCreate branch:\n{createBranch}";
    }

    [Description("Commit generated code to git.")]
    public async Task<string> GitCommit(string message)
    {
        var add = await _commandTools.RunCommand("git add .");
        var commit = await _commandTools.RunCommand($"git commit -m \"{message.Replace("\"", "'")}\"");
        return $"Git add:\n{add}\n\nGit commit:\n{commit}";
    }

    [Description("Push branch to remote without merging.")]
    public Task<string> PushBranch(string branchName)
    {
        var remoteName = _configuration["PullRequest:RemoteName"] ?? "origin";
        return _commandTools.RunCommand($"git push -u {remoteName} {branchName}");
    }
}
