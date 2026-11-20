using System.Diagnostics;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using Xunit;

namespace ICOGenerator.Tests.Artifacts;

// Cấu hình Git của dự án (popup Config delivery). Phần đáng giữ nhất là đường đưa token tới git: nó phải
// đi qua BIẾN MÔI TRƯỜNG chứ không qua đối số lệnh (đối số vào log gọi tool). Test credential helper GỌI
// GIT THẬT (`git credential fill` — không chạm mạng) để chứng minh helper khai báo bằng GIT_CONFIG_*
// thật sự trả đúng username/token, không chỉ "trông có vẻ đúng" trên giấy.
public class ProjectGitSettingsTests
{
    [Fact]
    public void From_EmptyProject_FallsBackToMainAndMachineCredentials()
    {
        var git = ProjectGitSettings.From(new Project());

        Assert.Equal("main", git.BaseBranch);
        Assert.False(git.HasCredential);
        Assert.Empty(git.CredentialEnvironment());
    }

    // Giá trị sai chỉ tới được đây bằng cách sửa thẳng DB (form đã chặn) — vẫn không được tới dòng lệnh git.
    [Fact]
    public void From_UnsafeBaseBranch_FallsBackToMain()
    {
        var git = ProjectGitSettings.From(new Project { GitBaseBranch = "--upload-pack=evil" });

        Assert.Equal("main", git.BaseBranch);
    }

    [Fact]
    public void CredentialEnvironment_CarriesTheToken_OnlyInEnvironmentValues()
    {
        var env = new ProjectGitSettings("develop", "NUU4HC", "secret-token").CredentialEnvironment();

        Assert.Equal("secret-token", env[ProjectGitSettings.TokenVariable]);
        Assert.Equal("NUU4HC", env[ProjectGitSettings.UsernameVariable]);
        // Helper chỉ THAM CHIẾU biến môi trường — chính nó không chứa bí mật.
        Assert.DoesNotContain("secret-token", env["GIT_CONFIG_VALUE_1"]);
    }

    [Fact]
    public async Task CredentialHelper_AnswersGitWithTheProjectAccount()
    {
        var env = new ProjectGitSettings("main", "NUU4HC", "secret-token").CredentialEnvironment();

        var output = await CredentialFillAsync(env);

        Assert.Contains("username=NUU4HC", output);
        Assert.Contains("password=secret-token", output);
    }

    // Chỉ nhập token (kiểu GitHub PAT): vẫn phải có một username, nếu không git hỏi tiếp và fail.
    [Fact]
    public async Task CredentialHelper_WithoutUsername_UsesFallbackUsername()
    {
        var env = new ProjectGitSettings("main", null, "secret-token").CredentialEnvironment();

        var output = await CredentialFillAsync(env);

        Assert.Contains($"username={ProjectGitSettings.FallbackUsername}", output);
        Assert.Contains("password=secret-token", output);
    }

    private static async Task<string> CredentialFillAsync(IReadOnlyDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("credential");
        psi.ArgumentList.Add("fill");
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var (key, value) in env)
            psi.Environment[key] = value;

        using var process = Process.Start(psi)!;
        await process.StandardInput.WriteAsync("protocol=https\nhost=git.example.com\n\n");
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"git credential fill thất bại: {stderr}");
        return stdout;
    }
}
