using ICOGenerator.Localization;
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

    /// <summary>Kho phiên giữ sống; null = runner không giữ phiên (mọi lượt chạy một phát).</summary>
    IClaudeCodeSessionPool? Sessions => null;
}

/// <summary>
/// Tìm và chạy lệnh <c>claude</c> (Claude Code CLI) đã cài và ĐÃ ĐĂNG NHẬP trên máy đang chạy app. Đây là thứ
/// cho phép dùng model Claude bằng gói Claude Pro/Max của người chạy máy thay vì API key: app không cầm
/// credential nào, mọi lời gọi đi qua chính CLI chính thức.
/// </summary>
public sealed class ClaudeCodeCli : IClaudeCodeRunner, IDisposable
{
    /// <summary>Scheme của ô Endpoint đánh dấu "model này chạy qua Claude Code CLI", vd. <c>claude-code://subscription</c>.</summary>
    public const string EndpointScheme = "claude-code";

    public const string MissingMessage =
        "Không thấy Claude Code CLI (lệnh `claude`) trên máy đang chạy app. Cài Claude Code, chạy `claude` một lần "
        + "để đăng nhập gói Pro/Max, rồi thử lại. Đã cài ở chỗ khác thì ghi đường dẫn vào Llm:ClaudeCode:BinaryPath.";

    private readonly string? _configuredPath;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly ClaudeCodeSessionPool _sessions;

    public ClaudeCodeCli(string? configuredPath = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        _configuredPath = configuredPath;
        _environment = environment ?? new Dictionary<string, string>();
        _sessions = new ClaudeCodeSessionPool(invocation => ClaudeCodeProcess.Start(FindBinary(_configuredPath), invocation, _environment));
    }

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

    /// <summary>
    /// Đặt đè <paramref name="overrides"/> (<c>Llm:ClaudeCode:Environment</c>) lên môi trường mà tiến trình
    /// <c>claude</c> thừa hưởng từ app. Đây là chỗ cấu hình mạng riêng cho CLI — nó KHÔNG đi qua HttpClient nên
    /// <c>Llm:Proxy</c> không với tới. Ca điển hình: <c>HTTPS_PROXY</c> của máy trỏ thẳng proxy công ty đòi
    /// NTLM/Kerberos (extension VS Code chạy được vì VS Code tự xác thực hộ), khai ở đây relay px/CNTLM local.
    /// Giá trị rỗng ⇒ gỡ hẳn biến. Mọi biến trùng tên KHÁC hoa thường cũng bị gỡ: trên Linux <c>https_proxy</c>
    /// và <c>HTTPS_PROXY</c> là hai biến, để sót bản cũ thì không biết CLI sẽ đọc bản nào.
    /// </summary>
    public static void ApplyEnvironment(IDictionary<string, string?> target, IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var (name, value) in overrides)
        {
            foreach (var existing in target.Keys.Where(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList())
                target.Remove(existing);
            if (value.Length > 0)
                target[name] = value;
        }
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception) { return false; }
    }

    public async IAsyncEnumerable<string> RunAsync(
        ClaudeCodeInvocation invocation, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Một phát: đưa MỘT dòng vào stdin, đóng stdin, đọc tới hết — CLI tự thoát sau lượt đó.
        using var process = ClaudeCodeProcess.Start(FindBinary(_configuredPath), invocation, _environment);
        await foreach (var line in process.RunToEndAsync(invocation.StdinLine, cancellationToken).ConfigureAwait(false))
            yield return line;
    }

    /// <summary>
    /// Các biến môi trường mà một phiên Claude Code ĐANG CHẠY đặt cho tiến trình con của nó. App được khởi
    /// động từ bên trong Claude Code (terminal của phiên, skill verify…) thì tiến trình <c>claude</c> của app
    /// thừa hưởng chúng và tự coi mình là phiên con của phiên đó. Đo thật (CLI 2.1.280, bắt request bằng một
    /// endpoint giả): có <c>CLAUDE_CODE_ENTRYPOINT</c> hoặc <c>CLAUDE_AGENT_SDK_VERSION</c> là MỖI lời gọi
    /// kéo theo một request "đặt tên phiên" chở lại TOÀN BỘ nội dung lượt đó — gấp đôi token đầu vào, không
    /// hiện trong call log vì nó không đi qua stream của lời gọi chính. <c>CLAUDE_EFFORT</c> thì đổi mức suy
    /// luận theo phiên cha. Gỡ trước khi áp <c>Llm:ClaudeCode:Environment</c> — ai cần thì khai lại ở đó.
    /// Cố ý là danh sách đóng chứ không gỡ cả nhóm <c>CLAUDE*</c>: <c>CLAUDE_CONFIG_DIR</c>,
    /// <c>CLAUDE_CODE_GIT_BASH_PATH</c>… là cấu hình thật của máy.
    /// </summary>
    internal static readonly string[] InheritedSessionVariables =
    [
        "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_AGENT_SDK_VERSION", "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_PID", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN",
        "CLAUDE_CODE_SESSION_ATTENDED", "CLAUDE_CODE_EXECPATH", "CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING",
        "CLAUDE_CODE_ENABLE_TASKS", "CLAUDE_EFFORT", "CLAUDE_CODE_SSE_PORT",
    ];

    /// <summary>Môi trường của tiến trình <c>claude</c>: bỏ dấu vết phiên cha, rồi áp cấu hình riêng.</summary>
    internal static void PrepareEnvironment(IDictionary<string, string?> target, IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var name in target.Keys
                     .Where(k => InheritedSessionVariables.Contains(k, StringComparer.OrdinalIgnoreCase))
                     .ToList())
            target.Remove(name);
        ApplyEnvironment(target, overrides);
    }

    // ── Phiên giữ sống ─────────────────────────────────────────────────────────────────────────────────

    public IClaudeCodeSessionPool Sessions => _sessions;

    public void Dispose() => _sessions.Dispose();
}

/// <summary>
/// Một tiến trình <c>claude -p --input-format stream-json</c> GIỮ SỐNG qua nhiều lượt: mỗi lượt là một dòng
/// stdin, CLI trả lời rồi chờ dòng kế. Hội thoại vì thế là các lượt THẬT trong bộ nhớ của CLI, và prompt cache
/// đọc lại được toàn bộ phần trước — xem <see cref="ClaudeCodeChatClient"/>.
/// </summary>
public interface IClaudeCodeSession : IDisposable
{
    /// <summary>Gửi một lượt user, trả từng dòng stdout tới hết sự kiện <c>result</c> của lượt đó.</summary>
    IAsyncEnumerable<string> SendAsync(string stdinLine, CancellationToken cancellationToken);
}

/// <summary>
/// Kho phiên giữ sống, khoá theo dấu vân tay hội thoại mà phiên đang giữ. Phiên được LẤY RA (không còn trong
/// kho) suốt lượt đang chạy, và chỉ được cất lại khi lượt kết thúc bằng lời gọi tool — tức là còn lượt sau.
/// </summary>
public interface IClaudeCodeSessionPool
{
    /// <summary>Lấy ra phiên đang giữ đúng hội thoại có khoá <paramref name="key"/>, hoặc null.</summary>
    IClaudeCodeSession? Take(string key);

    /// <summary>Mở tiến trình mới với đối số và system prompt của <paramref name="invocation"/> (chưa gửi lượt nào).</summary>
    IClaudeCodeSession Open(ClaudeCodeInvocation invocation);

    /// <summary>Cất phiên lại dưới khoá mới (hội thoại sau lượt vừa xong) để lượt kế tiếp tìm thấy.</summary>
    void Keep(string key, IClaudeCodeSession session);
}

/// <summary>
/// Kho phiên của <see cref="ClaudeCodeCli"/>. Có trần: phiên ngồi chờ quá <see cref="IdleTimeout"/> (vòng lặp
/// agent bỏ dở) hoặc vượt <see cref="MaxSessions"/> thì bị đóng — tiến trình bị giết. Trượt kho chỉ có nghĩa là
/// lượt đó chạy như cũ (phiên mới, kể lại lịch sử), không bao giờ là lỗi.
/// </summary>
internal sealed class ClaudeCodeSessionPool : IClaudeCodeSessionPool, IDisposable
{
    // Đủ dài cho một tool chậm giữa hai lượt (audit POC chạy trình duyệt mất vài phút); cache 1 giờ của CLI
    // còn sống lâu hơn thế.
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    internal const int MaxSessions = 6;

    private readonly Func<ClaudeCodeInvocation, IClaudeCodeSession> _open;
    private readonly Dictionary<string, (IClaudeCodeSession Session, DateTime Since)> _idle = new();
    private readonly object _gate = new();
    private readonly Timer _sweeper;
    private bool _disposed;

    public ClaudeCodeSessionPool(Func<ClaudeCodeInvocation, IClaudeCodeSession> open)
    {
        _open = open;
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        // Tiến trình con trên Windows không tự chết theo app: đóng hết khi app thoát.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public IClaudeCodeSession? Take(string key)
    {
        lock (_gate)
            return _idle.Remove(key, out var entry) ? entry.Session : null;
    }

    public IClaudeCodeSession Open(ClaudeCodeInvocation invocation) => _open(invocation);

    public void Keep(string key, IClaudeCodeSession session)
    {
        var evicted = new List<IClaudeCodeSession>();
        lock (_gate)
        {
            if (_disposed)
            {
                evicted.Add(session);
            }
            else
            {
                if (_idle.Remove(key, out var same))
                    evicted.Add(same.Session);
                _idle[key] = (session, DateTime.UtcNow);
                while (_idle.Count > MaxSessions)
                {
                    var oldest = _idle.MinBy(e => e.Value.Since);
                    _idle.Remove(oldest.Key);
                    evicted.Add(oldest.Value.Session);
                }
            }
        }
        evicted.ForEach(s => s.Dispose());
    }

    internal int IdleCount { get { lock (_gate) return _idle.Count; } }

    internal void Sweep(DateTime? now = null)
    {
        var cutoff = (now ?? DateTime.UtcNow) - IdleTimeout;
        var expired = new List<IClaudeCodeSession>();
        lock (_gate)
        {
            foreach (var key in _idle.Where(e => e.Value.Since < cutoff).Select(e => e.Key).ToList())
            {
                expired.Add(_idle[key].Session);
                _idle.Remove(key);
            }
        }
        expired.ForEach(s => s.Dispose());
    }

    public void Dispose()
    {
        List<IClaudeCodeSession> all;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            all = _idle.Values.Select(v => v.Session).ToList();
            _idle.Clear();
        }
        _sweeper.Dispose();
        all.ForEach(s => s.Dispose());
    }
}

/// <summary>
/// Một tiến trình <c>claude</c> cùng thư mục tạm của nó. Dùng chung cho lời gọi một phát
/// (<see cref="RunToEndAsync"/>) và phiên giữ sống (<see cref="SendAsync"/>).
/// </summary>
internal sealed class ClaudeCodeProcess : IClaudeCodeSession
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly string _workDir;
    private readonly Task<string> _stderr;
    private int _disposed;

    private ClaudeCodeProcess(Process process, string workDir)
    {
        _process = process;
        _workDir = workDir;
        // Đọc stderr suốt đời tiến trình: phiên giữ sống mà để đầy bộ đệm stderr là CLI treo.
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public static ClaudeCodeProcess Start(string? binary, ClaudeCodeInvocation invocation, IReadOnlyDictionary<string, string> environment)
    {
        if (binary is null)
            throw new InvalidOperationException(ClaudeCodeCli.MissingMessage);

        // Thư mục tạm RIÊNG cho mỗi tiến trình: chứa file system prompt (prompt nền của BA dài hơn giới hạn dòng
        // lệnh của Windows), và là cwd để CLI không nhặt CLAUDE.md/.claude của repo đang chạy app.
        var workDir = Path.Combine(Path.GetTempPath(), "icogen-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var systemFile = Path.Combine(workDir, "system.txt");
            File.WriteAllText(systemFile, invocation.SystemPrompt, Utf8NoBom);

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
            ClaudeCodeCli.PrepareEnvironment(start.Environment, environment);

            var process = Process.Start(start)
                ?? throw new InvalidOperationException(Loc.T("ClaudeCodeCli.RunAsync", binary));
            return new ClaudeCodeProcess(process, workDir);
        }
        catch
        {
            TryDeleteWorkDir(workDir);
            throw;
        }
    }

    public async IAsyncEnumerable<string> RunToEndAsync(string stdinLine, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var kill = cancellationToken.Register(Kill);
        await _process.StandardInput.WriteLineAsync(stdinLine.AsMemory(), cancellationToken).ConfigureAwait(false);
        _process.StandardInput.Close();

        while (await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            yield return line;

        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (_process.ExitCode != 0)
            throw await ExitedError().ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> SendAsync(string stdinLine, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Hủy (deadline của middleware, người dùng bấm dừng) ⇒ giết cả cây tiến trình, không để CLI chạy mồ côi.
        await using var kill = cancellationToken.Register(Kill);
        await _process.StandardInput.WriteLineAsync(stdinLine.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                // Tiến trình chết giữa lượt (lỗi của CLI vẫn được in thành một result trước khi thoát).
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                throw await ExitedError().ConfigureAwait(false);
            }

            yield return line;
            if (IsResult(line))
                yield break;
        }
    }

    // Dòng kết thúc một lượt của CLI: {"type":"result",…}. Dò rẻ trước khi parse.
    internal static bool IsResult(string line) =>
        line.StartsWith('{') && line.Contains("\"type\":\"result\"", StringComparison.Ordinal);

    private async Task<Exception> ExitedError()
    {
        var error = (await _stderr.ConfigureAwait(false)).Trim();
        return new InvalidOperationException(
            Loc.T("ClaudeCodeCli.RunAsync.2", _process.ExitCode, (error.Length > 0 ? " " + error : string.Empty)));
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception) { /* đã thoát */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        Kill();
        _process.Dispose();
        TryDeleteWorkDir(_workDir);
    }

    private static void TryDeleteWorkDir(string workDir)
    {
        try { Directory.Delete(workDir, recursive: true); }
        catch (Exception) { /* file tạm, hệ điều hành tự dọn */ }
    }
}
