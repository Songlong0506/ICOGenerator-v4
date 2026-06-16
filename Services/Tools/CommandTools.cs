using System.ComponentModel;
using System.Diagnostics;

namespace ICOGenerator.Services.Tools;

public class CommandTools
{
    private readonly IConfiguration _configuration;
    private readonly WorkspaceTools _workspaceTools;
    public CommandTools(IConfiguration configuration, WorkspaceTools workspaceTools)
    { _configuration = configuration; _workspaceTools = workspaceTools; }

    [Description("Run a safe shell command inside the current workspace.")]
    public async Task<string> RunCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(_workspaceTools.CurrentWorkspacePath)) throw new InvalidOperationException("Workspace is not initialized.");
        if (!IsAllowed(command)) return $"Command blocked for security reason: {command}";
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            Arguments = isWindows ? $"/c {command}" : $"-lc \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = _workspaceTools.CurrentWorkspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process == null) return "Cannot start process.";
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        // Keep a single reference to the wait task: calling WaitForExitAsync() again
        // returns a *different* Task instance, so comparing the WhenAny winner against a
        // fresh call would always be unequal and force every command into the timeout
        // branch (killing the process and discarding its real output).
        var waitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromMinutes(2)));
        if (completed != waitTask) { try { process.Kill(true); } catch { } return "Command timeout."; }
        return $"""
Command: {command}
ExitCode: {process.ExitCode}

Output:
{await outputTask}

Error:
{await errorTask}
""";
    }

    private bool IsAllowed(string command)
    {
        var allowed = _configuration.GetSection("AllowedCommands").Get<string[]>() ?? [];
        return allowed.Any(x => command.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    }
}
