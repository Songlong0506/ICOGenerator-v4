using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ICOGenerator.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace ICOGenerator.Services.Workflows;

/// <summary>Một lần chạy <c>opencode run</c>: thư mục làm việc, đối số, biến môi trường bổ sung, file log.</summary>
/// <param name="Secrets">
/// Chuỗi phải che (token relay) trước khi output đi vào file log, dòng tiến độ hay thông báo lỗi: client HTTP
/// in cả header <c>Authorization</c> khi một lời gọi hỏng, và đuôi output đó được chép vào cột Lỗi.
/// </param>
public sealed record PocOpencodeRun(
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string LogPath,
    TimeSpan Timeout,
    Action<string>? OnOutputLine,
    IReadOnlyList<string>? Secrets = null);

/// <summary>Kết cục một lần chạy. <see cref="OutputTail"/> = đoạn cuối stdout+stderr, để báo lỗi có ngữ cảnh.</summary>
public sealed record PocOpencodeRunResult(int ExitCode, bool TimedOut, string OutputTail)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>Chạy tiến trình opencode. Tách thành interface để test lái <see cref="PocOpencodeBuilder"/> không cần opencode thật.</summary>
public interface IPocOpencodeCli
{
    Task<PocOpencodeRunResult> RunAsync(PocOpencodeRun run, CancellationToken cancellationToken);
}

/// <summary>
/// Chạy <c>opencode</c> (lệnh lấy từ <see cref="PocOpencodeSettings.Command"/>) như một tiến trình con:
/// stdin đóng ngay (opencode đọc stdin khi nó không phải TTY — để mở là treo), stdout/stderr ghi nguyên văn
/// vào file log trong thư mục thử và từng dòng đẩy lên dòng tiến độ. Quá trần thời gian hoặc bị huỷ ⇒ dừng
/// CẢ CÂY tiến trình (opencode sinh tiến trình con cho tool <c>bash</c>).
/// </summary>
public sealed class PocOpencodeCli : IPocOpencodeCli
{
    public const string RedactedMarker = "***";

    private const int TailChars = 4000;

    // Mã màu/điều khiển ANSI trong output TUI — bỏ trước khi đưa lên dòng tiến độ.
    private static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private readonly PocOpencodeSettings _settings;

    public PocOpencodeCli(PocOpencodeSettings settings) => _settings = settings;

    public async Task<PocOpencodeRunResult> RunAsync(PocOpencodeRun run, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(_settings.Command)
        {
            WorkingDirectory = run.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var argument in run.Arguments)
            info.ArgumentList.Add(argument);
        foreach (var (key, value) in run.Environment)
            info.Environment[key] = value;

        using var process = new Process { StartInfo = info };
        var tail = new StringBuilder();
        await using var log = new StreamWriter(run.LogPath, append: true, Encoding.UTF8) { AutoFlush = true };
        var gate = new object();

        void OnLine(string? line)
        {
            if (line == null)
                return;
            line = Redact(line, run.Secrets);
            lock (gate)
            {
                log.WriteLine(line);
                tail.AppendLine(line);
                if (tail.Length > TailChars * 2)
                    tail.Remove(0, tail.Length - TailChars);
            }

            var clean = Ansi.Replace(line, string.Empty).Trim();
            if (clean.Length > 0)
            {
                try { run.OnOutputLine?.Invoke(clean); }
                catch { /* sink tiến độ hỏng không được làm hỏng lượt chạy */ }
            }
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Không chạy được lệnh opencode '{_settings.Command}' ({ex.Message}). Cài opencode trên máy chủ app "
                + "hoặc đặt PocOpencode:Command là đường dẫn đầy đủ (Windows: opencode.cmd).", ex);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(run.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* đã thoát giữa chừng */ }
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = true;
        }

        // Đợi luồng đọc output xả nốt (WaitForExit không đối số chờ cả hai luồng redirect).
        process.WaitForExit();
        string tailText;
        lock (gate)
            tailText = tail.Length > TailChars ? tail.ToString(tail.Length - TailChars, TailChars) : tail.ToString();
        return new PocOpencodeRunResult(timedOut ? -1 : process.ExitCode, timedOut, tailText);
    }

    internal static string Redact(string line, IReadOnlyList<string>? secrets)
    {
        if (secrets == null)
            return line;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
                line = line.Replace(secret, RedactedMarker, StringComparison.Ordinal);
        }
        return line;
    }
}

/// <summary>
/// Địa chỉ gốc mà opencode gọi ngược relay của app: <see cref="PocOpencodeSettings.RelayBaseUrl"/> nếu có,
/// ngược lại địa chỉ <c>http://</c> đầu tiên Kestrel đang nghe (host đại diện <c>+</c>/<c>*</c>/<c>0.0.0.0</c>/
/// <c>[::]</c> đổi về <c>127.0.0.1</c> — opencode chạy cùng máy). Chỉ HTTPS mà không cấu hình ⇒ <c>null</c>:
/// chứng chỉ máy dev không được tiến trình ngoài tin, và tắt kiểm chứng chỉ không phải lựa chọn.
/// </summary>
public sealed class PocOpencodeRelayAddress
{
    private static readonly Regex WildcardHost = new(@"^(http://)(\+|\*|0\.0\.0\.0|\[::\])(:|/|$)", RegexOptions.Compiled);

    private readonly PocOpencodeSettings _settings;
    private readonly IServer? _server;

    public PocOpencodeRelayAddress(PocOpencodeSettings settings, IServer? server = null)
    {
        _settings = settings;
        _server = server;
    }

    public string? Resolve()
    {
        if (!string.IsNullOrWhiteSpace(_settings.RelayBaseUrl))
            return _settings.RelayBaseUrl.Trim().TrimEnd('/');

        var addresses = _server?.Features.Get<IServerAddressesFeature>()?.Addresses;
        var http = addresses?.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        return http == null ? null : WildcardHost.Replace(http, "${1}127.0.0.1${3}").TrimEnd('/');
    }
}
