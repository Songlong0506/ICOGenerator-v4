namespace ICOGenerator.Application.Settings;

public class AppSettingsVm
{
    public string ConnectionString { get; set; } = "";
    public string WorkspaceRootPath { get; set; } = "";

    /// <summary>One command per line, e.g. "dotnet" or "git status".</summary>
    public string AllowedCommands { get; set; } = "";

    /// <summary>One extension per line, e.g. ".cs".</summary>
    public string AllowedFileExtensions { get; set; } = "";

    public string PullRequestRemoteName { get; set; } = "origin";
    public string AllowedHosts { get; set; } = "*";
}
