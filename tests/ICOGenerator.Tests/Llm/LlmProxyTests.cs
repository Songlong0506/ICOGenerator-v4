using System.Net;
using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Proxy sai không bao giờ báo lỗi lúc khởi động: nó lặng lẽ biến MỌI lời gọi LLM thành "Retry failed after
// 4 tries" trên máy người khác. Hai chỗ dễ sai nhất được khóa ở đây: app có trả lời nổi 407 của proxy công
// ty không, và danh sách bypass có khớp ĐÚNG những host định cho đi thẳng không.
public class LlmProxyTests
{
    private static WebProxy Build(bool useDefaultCredentials = false, params string[] bypass) =>
        Assert.IsType<WebProxy>(LlmProxy.Create(new LlmSettings(
            proxyEnabled: true,
            proxyAddress: "http://rb-proxy.example.com:8080",
            proxyUseDefaultCredentials: useDefaultCredentials,
            proxyBypassList: bypass)));

    [Fact]
    public void Create_WhenProxyDisabled_ReturnsNullSoTheHandlerGoesDirect()
    {
        Assert.Null(LlmProxy.Create(new LlmSettings(proxyEnabled: false)));
    }

    // Mặc định KHÔNG gửi danh tính Windows đi đâu cả — relay local (px/CNTLM) tự lo phần xác thực.
    [Fact]
    public void Create_ByDefault_SendsNoCredentials()
    {
        Assert.Null(Build().Credentials);
    }

    [Fact]
    public void Create_WithUseDefaultCredentials_AnswersProxyAuthWithTheWindowsAccount()
    {
        Assert.Same(CredentialCache.DefaultCredentials, Build(useDefaultCredentials: true).Credentials);
    }

    // Hậu tố tên miền: đúng cái người ta gõ trong no_proxy.
    [Theory]
    [InlineData(".bosch.com")]
    [InlineData("*.bosch.com")]
    public void BypassList_WithDomainSuffix_SkipsTheProxyForThatDomainAndItsSubdomains(string entry)
    {
        var proxy = Build(bypass: entry);

        Assert.True(proxy.IsBypassed(new Uri("https://llm.internal.bosch.com/v1")));
        Assert.True(proxy.IsBypassed(new Uri("http://bosch.com:8000/v1")));
        Assert.False(proxy.IsBypassed(new Uri("https://api.openai.com/v1")));
    }

    // Cái bẫy của WebProxy: BypassList nhận REGEX. Để nguyên chuỗi người dùng gõ thì dấu chấm thành "ký tự
    // bất kỳ" và một host lạ cũng lọt qua proxy — sai theo hướng KHÔNG ai phát hiện ra, vì lời gọi vẫn chạy.
    [Fact]
    public void BypassList_TreatsDotsLiterally_NotAsRegexWildcards()
    {
        var proxy = Build(bypass: ".bosch.com");

        Assert.False(proxy.IsBypassed(new Uri("https://evil-bosch.com/v1")));
        Assert.False(proxy.IsBypassed(new Uri("https://boschxcom.example.net/v1")));
    }

    [Fact]
    public void BypassList_WithExactHost_DoesNotLeakToItsSubdomains()
    {
        var proxy = Build(bypass: "llm.corp.local");

        Assert.True(proxy.IsBypassed(new Uri("http://llm.corp.local:1234/v1")));
        Assert.False(proxy.IsBypassed(new Uri("http://other.llm.corp.local:1234/v1")));
    }

    // Mục rỗng trong appsettings (dòng thừa lúc sửa JSON) không được biến thành regex khớp mọi thứ — nếu
    // không thì bật proxy xong hoá ra chẳng có gì đi qua proxy.
    [Fact]
    public void BypassList_IgnoresBlankEntries()
    {
        var proxy = Build(bypass: new[] { "", "   " });

        Assert.False(proxy.IsBypassed(new Uri("https://api.openai.com/v1")));
    }
}
