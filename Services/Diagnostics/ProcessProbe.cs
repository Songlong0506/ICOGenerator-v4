using System.ComponentModel;
using System.Diagnostics;

namespace ICOGenerator.Services.Diagnostics;

/// <summary>
/// Chạy một lệnh CỐ ĐỊNH do chính code kiểm tra đặt ra (<c>git --version</c>, <c>dotnet --list-sdks</c>…) và
/// lấy output. Không có đầu vào nào từ người dùng hay model ⇒ không cần allowlist; đối số vẫn đi qua
/// <see cref="ProcessStartInfo.ArgumentList"/>.
/// </summary>
public static class ProcessProbe
{
    /// <param name="Started">false = không khởi chạy được (không có trong PATH của tài khoản chạy app).</param>
    public sealed record Result(bool Started, int ExitCode, string Output);

    public static async Task<Result> RunAsync(
        string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            return new Result(false, -1, ex.Message);
        }

        if (process is null)
            return new Result(false, -1, string.Empty);

        using (process)
        {
            using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderr = process.StandardError.ReadToEndAsync(linked.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
                var output = (await stdout).Trim();
                var error = (await stderr).Trim();
                return new Result(true, process.ExitCode, output.Length > 0 ? output : error);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { /* đã thoát */ }
                if (cancellationToken.IsCancellationRequested)
                    throw;
                return new Result(true, -1, "timeout");
            }
        }
    }

    /// <summary>
    /// Lệnh kiểu <c>npm</c> trên Windows là <c>npm.cmd</c> — <see cref="Process.Start(ProcessStartInfo)"/> không
    /// tự thêm <c>.cmd</c>, nên phải đi qua <c>cmd.exe /c</c> (giống tool <c>RunCommand</c> của agent).
    /// </summary>
    public static Task<Result> RunShimAsync(string command, IReadOnlyList<string> args, CancellationToken cancellationToken)
        => OperatingSystem.IsWindows()
            ? RunAsync("cmd.exe", ["/c", command, .. args], cancellationToken)
            : RunAsync(command, args, cancellationToken);

    /// <summary>Dòng đầu, gọn — đủ để hiện trong một ô bảng.</summary>
    public static string FirstLine(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        var newline = value.IndexOfAny(['\r', '\n']);
        return newline < 0 ? value : value[..newline].Trim();
    }
}
