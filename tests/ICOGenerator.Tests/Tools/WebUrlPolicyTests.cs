using ICOGenerator.Services.Browser;
using Xunit;

namespace ICOGenerator.Tests.Tools;

// WebPilot cố ý KHÔNG có allowlist domain (kịch bản đích gồm cả trang nội bộ của công ty), nên chốt
// chặn duy nhất còn lại là SCHEME — và nó phải giữ. Đặc biệt file: — trình duyệt mà mở được file://
// là một đường vòng đọc đĩa, qua mặt AllowedFileExtensions mà mọi tool file đều phải đi qua.
public class WebUrlPolicyTests
{
    [Theory]
    [InlineData("https://www.google.com/search?q=a")]
    [InlineData("http://intranet.bosch.com/form")]
    [InlineData("http://10.1.2.3:8080/excel")]        // mạng nội bộ PHẢI qua được — đó là kịch bản đích
    [InlineData("http://localhost:5000/")]
    public void Accepts_HttpAndHttps_IncludingPrivateNetwork(string url)
    {
        var (safe, error) = WebUrlPolicy.Validate(url);

        Assert.Null(error);
        Assert.NotNull(safe);
    }

    [Fact]
    public void AddsHttps_WhenSchemeMissing()
    {
        var (safe, error) = WebUrlPolicy.Validate("example.com/abc");

        Assert.Null(error);
        Assert.StartsWith("https://example.com/abc", safe);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<h1>x</h1>")]
    [InlineData("about:blank")]
    public void Rejects_NonHttpSchemes(string url)
    {
        var (safe, error) = WebUrlPolicy.Validate(url);

        Assert.Null(safe);
        Assert.NotNull(error);
    }

    [Fact]
    public void Rejects_CloudMetadataEndpoint()
    {
        var (safe, error) = WebUrlPolicy.Validate("http://169.254.169.254/latest/meta-data/");

        Assert.Null(safe);
        Assert.Contains("bị chặn", error);
    }

    [Fact]
    public void Rejects_EmbeddedCredentials()
    {
        // Credential nhúng trong URL sẽ nằm nguyên văn trong log lời gọi tool.
        var (safe, error) = WebUrlPolicy.Validate("https://admin:secret@intranet/report");

        Assert.Null(safe);
        Assert.Contains("mật khẩu", error);
    }

    [Fact]
    public void Rejects_Empty()
    {
        Assert.NotNull(WebUrlPolicy.Validate("   ").Error);
    }
}
