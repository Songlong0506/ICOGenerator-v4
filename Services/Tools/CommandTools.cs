using System.ComponentModel;
using System.Diagnostics;

namespace ICOGenerator.Services.Tools;

public class CommandTools
{
    private readonly IConfiguration _configuration;
    private readonly WorkspaceTools _workspaceTools;
    private readonly int _timeoutSeconds;

    public CommandTools(IConfiguration configuration, WorkspaceTools workspaceTools)
    {
        _configuration = configuration;
        _workspaceTools = workspaceTools;
        // Hard per-command ceiling, configurable (Commands:TimeoutSeconds) so a slow build/install
        // of a larger POC isn't cut off at a fixed 2 minutes.
        _timeoutSeconds = configuration.GetValue("Commands:TimeoutSeconds", 120);
    }

    [Description("Run a safe shell command inside the current workspace.")]
    public Task<string> RunCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(_workspaceTools.CurrentWorkspacePath)) throw new InvalidOperationException("Workspace is not initialized.");
        // The allowlist only matches the command PREFIX and runs via a shell, so "git status && …"
        // or "git status; curl…|bash" would pass the check yet chain arbitrary commands. Reject
        // shell control/redirection/substitution operators so the allowlist actually holds.
        if (ContainsShellOperators(command)) return Task.FromResult($"Command blocked for security reason (shell operators are not allowed): {command}");
        if (ContainsInlineCodeEval(command)) return Task.FromResult($"Command blocked for security reason (inline code execution is not allowed): {command}");
        if (!IsAllowed(command)) return Task.FromResult($"Command blocked for security reason: {command}");
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            WorkingDirectory = _workspaceTools.CurrentWorkspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (isWindows)
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            // "-c" (NOT "-lc"): a login shell sources the profile and enables history expansion ("!"),
            // widening what a command can trigger. Pass via ArgumentList so .NET handles quoting safely.
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        return ExecuteAsync(psi, command);
    }

    // Run an allowlisted command with arguments passed LITERALLY (no shell), so operators like
    // & | ; $ ` < > inside an argument are inert data (e.g. a commit message can contain them).
    // The allowlist and inline-eval guards still apply to the executable+flags prefix.
    public Task<string> RunArgs(IReadOnlyList<string> args)
    {
        if (string.IsNullOrWhiteSpace(_workspaceTools.CurrentWorkspacePath)) throw new InvalidOperationException("Workspace is not initialized.");
        if (args == null || args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
            return Task.FromResult("Command blocked for security reason: empty command.");

        // The allowlist matches the bare executable name, so reject a path-qualified first token
        // ("/tmp/evil/git", "./node") that could point FileName at an attacker-placed binary.
        if (args[0].Contains('/') || args[0].Contains('\\'))
            return Task.FromResult($"Command blocked for security reason (executable must be a bare name, not a path): {args[0]}");

        var commandLine = string.Join(' ', args);
        if (ContainsInlineCodeEval(commandLine)) return Task.FromResult($"Command blocked for security reason (inline code execution is not allowed): {commandLine}");
        if (!IsAllowed(commandLine)) return Task.FromResult($"Command blocked for security reason: {commandLine}");

        var psi = new ProcessStartInfo
        {
            FileName = args[0],
            WorkingDirectory = _workspaceTools.CurrentWorkspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        for (var i = 1; i < args.Count; i++)
            psi.ArgumentList.Add(args[i]);

        return ExecuteAsync(psi, commandLine);
    }

    // Hard cap on how much of each stream we keep in memory, so a runaway command (build loop, huge
    // tree listing) can't OOM the worker. We still drain the rest so the child never blocks on a full pipe.
    private const int MaxOutputChars = 100_000;

    private async Task<string> ExecuteAsync(ProcessStartInfo psi, string displayCommand)
    {
        using var process = Process.Start(psi);
        if (process == null) return "Cannot start process.";
        var outputTask = ReadCappedAsync(process.StandardOutput);
        var errorTask = ReadCappedAsync(process.StandardError);
        // Keep one reference to the wait task: WaitForExitAsync() returns a *different* Task each
        // call, so a fresh call would never equal the WhenAny winner and force every command to time out.
        // Task.Delay also observes the run token, so it completes on a cancel/shutdown, not only on timeout.
        var runToken = _workspaceTools.RunCancellationToken;
        var waitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(_timeoutSeconds), runToken));
        if (completed != waitTask)
        {
            try { process.Kill(true); } catch { }
            // Cancellation (workflow cancel / app shutdown) must propagate so the run is treated as
            // interrupted, not as a plain command timeout that the agent could "retry".
            if (runToken.IsCancellationRequested) throw new OperationCanceledException(runToken);
            return "Command timeout.";
        }
        return $"""
Command: {displayCommand}
ExitCode: {process.ExitCode}

Output:
{await outputTask}

Error:
{await errorTask}
""";
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var buffer = new char[8192];
        var sb = new System.Text.StringBuilder();
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            if (sb.Length >= MaxOutputChars) { truncated = true; continue; } // keep draining, stop storing
            var remaining = MaxOutputChars - sb.Length;
            sb.Append(buffer, 0, Math.Min(read, remaining));
            if (read > remaining) truncated = true;
        }
        if (truncated) sb.Append("\n…[output truncated]");
        return sb.ToString();
    }

    private bool IsAllowed(string command)
    {
        var allowed = _configuration.GetSection("AllowedCommands").Get<string[]>() ?? [];
        // Khớp theo ranh giới "từ": lệnh phải bằng đúng entry hoặc entry + khoảng trắng + tham số.
        // StartsWith trần sẽ cho qua cả "npmEVIL" cho entry "npm" (rò rỉ allowlist).
        return allowed.Any(x =>
            command.Equals(x, StringComparison.OrdinalIgnoreCase)
            || command.StartsWith(x + " ", StringComparison.OrdinalIgnoreCase));
    }

    // Shell operators that chain/redirect/substitute commands (& | ; ` $ < > and newlines).
    // No allowed command needs these, so blocking them keeps execution to one allowlisted command.
    private static readonly char[] ShellOperators = { '&', '|', ';', '`', '$', '<', '>', '\n', '\r' };

    private static bool ContainsShellOperators(string command) =>
        command.IndexOfAny(ShellOperators) >= 0;

    // Interpreters like node/dotnet are allowlisted (needed to build/run a POC), but a few flags
    // turn them into arbitrary-code evaluators (`node -e`, `dotnet fsi`) with no shell operator,
    // sailing past every other guard. Block those inline-eval forms while permitting normal usage.
    private static bool ContainsInlineCodeEval(string command)
    {
        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        var exe = Path.GetFileNameWithoutExtension(tokens[0]).ToLowerInvariant();

        return exe switch
        {
            "node" or "nodejs" => tokens.Skip(1).Any(t =>
                t is "-e" or "--eval" or "-p" or "--print"
                || t.StartsWith("--eval=", StringComparison.Ordinal)
                || t.StartsWith("--print=", StringComparison.Ordinal)),
            "dotnet" => tokens.Length > 1
                && (tokens[1].Equals("fsi", StringComparison.OrdinalIgnoreCase)
                    || tokens[1].Equals("script", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }
}
