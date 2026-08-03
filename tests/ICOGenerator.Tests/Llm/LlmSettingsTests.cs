using ICOGenerator.Services.Llm;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// LlmSettings là nguồn DUY NHẤT của section "Llm" (trước đây LlmClient/AgentRunService/EvalRunnerService
// mỗi nơi tự đọc kèm hằng mặc định riêng). Khoá lại giá trị mặc định + cách đọc config để đổi nhầm một
// chỗ là test đỏ, thay vì ba đường gọi model lặng lẽ chạy với ba deadline khác nhau.
public class LlmSettingsTests
{
    private static LlmSettings From(params (string Key, string Value)[] values) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build());

    [Fact]
    public void Defaults_PreserveTheValuesEachServiceUsedToHardcode()
    {
        var settings = From();

        Assert.Equal(600, settings.RequestTimeoutSeconds);
        Assert.Equal(30, settings.TestConnectionTimeoutSeconds);
        Assert.True(settings.ProxyEnabled);
        Assert.Equal("http://127.0.0.1:3128", settings.ProxyAddress);
    }

    [Fact]
    public void ReadsEveryKeyFromTheLlmSection()
    {
        var settings = From(
            ("Llm:RequestTimeoutSeconds", "90"),
            ("Llm:TestConnectionTimeoutSeconds", "5"),
            ("Llm:Proxy:Enabled", "false"),
            ("Llm:Proxy:Address", "http://proxy.local:8080"));

        Assert.Equal(90, settings.RequestTimeoutSeconds);
        Assert.Equal(5, settings.TestConnectionTimeoutSeconds);
        Assert.False(settings.ProxyEnabled);
        Assert.Equal("http://proxy.local:8080", settings.ProxyAddress);
    }

    // Deadline <= 0 sẽ hủy mọi lời gọi ngay lập tức (mọi lượt agent/chat chết câm) — coi cấu hình xấu như
    // "dùng mặc định" thay vì làm app vô dụng.
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void NonPositiveTimeouts_FallBackToDefaults(string value)
    {
        var settings = From(
            ("Llm:RequestTimeoutSeconds", value),
            ("Llm:TestConnectionTimeoutSeconds", value));

        Assert.Equal(600, settings.RequestTimeoutSeconds);
        Assert.Equal(30, settings.TestConnectionTimeoutSeconds);
    }
}
