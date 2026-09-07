using System.Diagnostics;

namespace ICOGenerator.Services.Artifacts;

/// <summary>
/// Chạy một lệnh <c>git</c> ở TẦNG HẠ TẦNG — tức các lệnh do CHÍNH hệ thống phát ra khi chuẩn bị
/// workspace (clone repo dự án, clone skeleton, đặt remote), KHÔNG phải lệnh do agent gọi.
/// <para>
/// Vì sao không đi qua <see cref="Tools.CommandTools"/> như tool của agent: các lệnh ở đây chạy TRƯỚC
/// khi agent (và <c>WorkspaceTools.CurrentWorkspacePath</c>) được khởi tạo, và tham số của chúng do
/// cấu hình/DB sinh ra chứ không do model sinh ra — nên chúng không cần allowlist theo prefix, chỉ cần
/// đúng một điều: không bao giờ đi qua shell. Mọi đối số vào thẳng
/// <see cref="ProcessStartInfo.ArgumentList"/>.
/// </para>
/// </summary>
internal static class GitCli
{
    public static async Task<(int ExitCode, string Output)> RunAsync(
        IReadOnlyList<string> args, string? workingDirectory, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        // Không có TTY: remote HTTPS thiếu credential sẽ làm git NGỒI CHỜ nhập user/mật khẩu và treo
        // luôn cả task nền. Tắt prompt để nó fail ngay với đúng lý do (xem CommandTools, cùng lý lẽ).
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Không khởi chạy được 'git'. Máy chủ đã cài git chưa?");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
    }
}
