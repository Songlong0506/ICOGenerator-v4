using System.ComponentModel;
namespace ICOGenerator.Services.Tools;
public class DiffTools
{
    private readonly CommandTools _commandTools;
    public DiffTools(CommandTools commandTools) { _commandTools = commandTools; }
    [Description("Show git diff/status for current workspace.")]
    public Task<string> GitDiff() => _commandTools.RunCommand("git diff --stat");
}
