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
        // Hard ceiling for any single command. Configurable (Commands:TimeoutSeconds) so a slow
        // `dotnet build` / `npm install` of a larger POC isn't cut off at a fixed 2 minutes,
        // mirroring how the LLM request timeout is already configurable.
        _timeoutSeconds = configuration.GetValue("Commands:TimeoutSeconds", 120);
    }

    [Description("Run a safe shell command inside the current workspace.")]
    public Task<string> RunCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(_workspaceTools.CurrentWorkspacePath)) throw new InvalidOperationException("Workspace is not initialized.");
        // The allowlist below only matches the command PREFIX, and the command is run through
        // a shell. Without this guard, "git status && <anything>" or "git status; curl…|bash"
        // would pass the prefix check yet let the shell run arbitrary chained commands. Reject
        // any shell control/redirection/substitution operators so the allowlist actually holds.
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
            // "-c" (NOT "-lc"): a login shell ("-l") sources the user's profile and enables
            // history expansion ("!"), widening what an allowlisted command can trigger. Pass the
            // command via ArgumentList so .NET does the quoting — the previous manual
            // `command.Replace("\"","\\\"")` mishandled backslashes and could break out of quoting.
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        return ExecuteAsync(psi, command);
    }

    // Run an allowlisted command with its arguments passed LITERALLY (no shell). Because no
    // shell parses the arguments, operators such as & | ; $ ` < > inside an argument are inert
    // data — so a git commit message or branch name containing them is no longer rejected by
    // the shell-operator guard (which only the shell-based RunCommand needs). The allowlist and
    // inline-eval guards still apply, enforced against the executable + flags prefix; an
    // argument can only EXTEND that prefix, never widen which executable runs.
    public Task<string> RunArgs(IReadOnlyList<string> args)
    {
        if (string.IsNullOrWhiteSpace(_workspaceTools.CurrentWorkspacePath)) throw new InvalidOperationException("Workspace is not initialized.");
        if (args == null || args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
            return Task.FromResult("Command blocked for security reason: empty command.");

        // The allowlist matches on the bare executable name (e.g. "git"), so a path-qualified
        // first token ("/tmp/evil/git", "./node") must be rejected — otherwise it would either
        // miss the allowlist or, worse, point FileName at an attacker-placed binary that merely
        // shares a name with an allowed one.
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

    private async Task<string> ExecuteAsync(ProcessStartInfo psi, string displayCommand)
    {
        using var process = Process.Start(psi);
        if (process == null) return "Cannot start process.";
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        // Keep a single reference to the wait task: calling WaitForExitAsync() again
        // returns a *different* Task instance, so comparing the WhenAny winner against a
        // fresh call would always be unequal and force every command into the timeout
        // branch (killing the process and discarding its real output).
        var waitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(_timeoutSeconds)));
        if (completed != waitTask) { try { process.Kill(true); } catch { } return "Command timeout."; }
        return $"""
Command: {displayCommand}
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
        // Khớp theo ranh giới "từ": lệnh phải bằng đúng entry hoặc là entry + khoảng trắng + tham số.
        // Nếu chỉ dùng StartsWith trần, entry "npm" sẽ cho qua cả "npmEVIL" (một executable khác
        // trùng tiền tố), làm rò rỉ allowlist. Cách này vẫn chấp nhận mọi cách dùng hợp lệ
        // ("git status -s", "dotnet build", "npm install"...).
        return allowed.Any(x =>
            command.Equals(x, StringComparison.OrdinalIgnoreCase)
            || command.StartsWith(x + " ", StringComparison.OrdinalIgnoreCase));
    }

    // Operators a shell would interpret to chain, redirect, or substitute commands:
    // & | ; ` $ < > and newlines. None of the allowed commands need these, so blocking
    // them keeps execution to a single allowlisted command.
    private static readonly char[] ShellOperators = { '&', '|', ';', '`', '$', '<', '>', '\n', '\r' };

    private static bool ContainsShellOperators(string command) =>
        command.IndexOfAny(ShellOperators) >= 0;

    // The allowlist contains general-purpose interpreters (node, dotnet) because the agent
    // legitimately needs them to build/run a generated POC. But a few of their flags turn
    // the interpreter into an arbitrary-code evaluator (e.g. `node -e "<any JS>"`,
    // `dotnet fsi`) which contains no shell operator and would sail past every other guard,
    // making the allowlist meaningless. Block those specific inline-eval forms while still
    // permitting normal usage (dotnet build/run, node <script.js>, npm install).
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
