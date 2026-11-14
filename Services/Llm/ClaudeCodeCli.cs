using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace ICOGenerator.Services.Llm;

/// <summary>Một lần chạy <c>claude -p</c>: đối số, system prompt (ghi ra file) và dòng JSON đưa vào stdin.</summary>
/// <param name="Arguments">Đối số của CLI, KHÔNG gồm <c>--system-prompt-file</c> (runner tự thêm kèm đường dẫn file tạm).</param>
public sealed record ClaudeCodeInvocation(IReadOnlyList<string> Arguments, string SystemPrompt, string StdinLine);

/// <summary>Chạy Claude Code CLI và trả từng dòng stdout (mỗi dòng một sự kiện stream-json). Tách ra để test thay được.</summary>
public interface IClaudeCodeRunner
{
    IAsyncEnumerable<string> RunAsync(ClaudeCodeInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>
/// Tìm và chạy lệnh <c>claude</c> (Claude Code CLI) đã cài và ĐÃ ĐĂNG NHẬP trên máy đang chạy app. Đây là thứ
/// cho phép dùng model Claude bằng gói Claude Pro/Max của người chạy máy thay vì API key: app không cầm
/// credential nào, mọi lời gọi đi qua chính CLI chính thức.
/// </summary>
public sealed class ClaudeCodeCli : IClaudeCodeRunner
{
    /// <summary>Scheme của ô Endpoint đánh dấu "model này chạy qua Claude Code CLI", vd. <c>claude-code://subscription</c>.</summary>
    public const string EndpointScheme = "claude-code";

    public const string MissingMessage =
        "Không thấy Claude Code CLI (lệnh `claude`) trên máy đang chạy app. Cài Claude Code, chạy `claude` một lần "
        + "để đăng nhập gói Pro/Max, rồi thử lại. Đã cài ở chỗ khác thì ghi đường dẫn vào Llm:ClaudeCode:BinaryPath.";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string? _configuredPath;

    public ClaudeCodeCli(string? configuredPath = null) => _configuredPath = configuredPath;

    public static bool IsClaudeCodeEndpoint(string? endpoint) =>
        OpenAiCompatibility.UriOf(endpoint) is { } uri
        && uri.Scheme.Equals(EndpointScheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Đường dẫn lệnh <c>claude</c>, hoặc null khi không thấy. Thứ tự: cấu hình → PATH → các chỗ trình cài hay
    /// đặt → bản đi kèm extension Claude Code của VS Code (extension không tự thêm vào PATH). Trên Windows ưu
    /// tiên <c>claude.exe</c> hơn <c>claude.cmd</c>: đối số đi qua cmd.exe bị hỏng dấu ngoặc kép.
    /// </summary>
    public static string? FindBinary(string? configuredPath)
    {
        var own = Environment.ExpandEnvironmentVariables(configuredPath?.Trim().Trim('"') ?? string.Empty);
        if (own.Length > 0)
            return File.Exists(own) ? own : null;

        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : new[] { "claude" };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        dirs.AddRange(new[]
        {
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".claude", "local"),
            Path.Combine(home, ".npm-global", "bin"),
            Path.Combine(home, ".bun", "bin"),
            "/usr/local/bin",
            "/opt/homebrew/bin",
        });
        foreach (var variable in new[] { "APPDATA", "LOCALAPPDATA" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root)
                dirs.Add(Path.Combine(root, "npm"));

        foreach (var name in names)
            foreach (var dir in dirs)
                if (SafeExists(Path.Combine(dir, name)))
                    return Path.Combine(dir, name);

        var bundled = new List<string>();
        foreach (var editor in new[] { ".vscode", ".vscode-insiders", ".cursor", ".windsurf" })
        {
            var extensions = Path.Combine(home, editor, "extensions");
            if (!Directory.Exists(extensions))
                continue;
            foreach (var extension in Directory.EnumerateDirectories(extensions, "anthropic.claude-code-*"))
                foreach (var name in names)
                    if (SafeExists(Path.Combine(extension, "resources", "native-binary", name)))
                        bundled.Add(Path.Combine(extension, "resources", "native-binary", name));
        }
        return bundled.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception) { return false; }
    }

    public async IAsyncEnumerable<string> RunAsync(
        ClaudeCodeInvocation invocation, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var binary = FindBinary(_configuredPath) ?? throw new InvalidOperationException(MissingMessage);

        // Thư mục tạm RIÊNG cho mỗi lời gọi: chứa file system prompt (prompt nền của BA dài hơn giới hạn dòng
        // lệnh của Windows), và là cwd để CLI không nhặt CLAUDE.md/.claude của repo đang chạy app.
        var workDir = Path.Combine(Path.GetTempPath(), "icogen-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var systemFile = Path.Combine(workDir, "system.txt");
            await File.WriteAllTextAsync(systemFile, invocation.SystemPrompt, Utf8NoBom, cancellationToken).ConfigureAwait(false);

            var start = new ProcessStartInfo(binary)
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Utf8NoBom,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in invocation.Arguments)
                start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--system-prompt-file");
            start.ArgumentList.Add(systemFile);

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException($"Không chạy được Claude Code CLI: {binary}");
            // Hủy (deadline của middleware, người dùng bấm dừng) ⇒ giết cả cây tiến trình, không để CLI chạy mồ côi.
            await using var kill = cancellationToken.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception) { /* đã thoát */ }
            });

            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteLineAsync(invocation.StdinLine.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                yield return line;

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var error = (await stderr.ConfigureAwait(false)).Trim();
                throw new InvalidOperationException(
                    $"Claude Code CLI thoát với mã {process.ExitCode}." + (error.Length > 0 ? " " + error : string.Empty));
            }
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (Exception) { /* file tạm, hệ điều hành tự dọn */ }
        }
    }
}
