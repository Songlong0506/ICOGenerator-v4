using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// The non-standard "thinking": { "type": "disabled" } field turns off reasoning on models that honour
// it (e.g. DeepSeek), but the official OpenAI API 400s on unknown parameters. These tests lock in that
// the handler injects the field for compatible endpoints and skips it for *.openai.com.
public class ThinkingDisabledHandlerTests
{
    // Captures the body actually forwarded downstream so we can assert on what the model would receive.
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static async Task<string?> SendAsync(string url, string body)
    {
        var capture = new CapturingHandler();
        var handler = new ThinkingDisabledHandler { InnerHandler = capture };
        using var invoker = new HttpMessageInvoker(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        await invoker.SendAsync(request, CancellationToken.None);
        return capture.CapturedBody;
    }

    [Theory]
    [InlineData("https://api.deepseek.com/v1/chat/completions")]
    [InlineData("http://localhost:1234/v1/chat/completions")]
    public async Task Injects_Thinking_For_NonOpenAI_Endpoints(string url)
    {
        var sent = await SendAsync(url, """{"model":"deepseek-chat","stream":true}""");

        var obj = Assert.IsType<JsonObject>(JsonNode.Parse(sent!));
        Assert.Equal("disabled", obj["thinking"]?["type"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/chat/completions")]
    [InlineData("https://eastus.api.openai.com/v1/chat/completions")]
    public async Task Skips_Thinking_For_OpenAI_Endpoints(string url)
    {
        var sent = await SendAsync(url, """{"model":"gpt-5-nano","stream":true}""");

        var obj = Assert.IsType<JsonObject>(JsonNode.Parse(sent!));
        Assert.False(obj.ContainsKey("thinking"));
    }

    [Fact]
    public async Task Leaves_Existing_Thinking_Field_Untouched()
    {
        var sent = await SendAsync(
            "https://api.deepseek.com/v1/chat/completions",
            """{"model":"deepseek-chat","thinking":{"type":"enabled"}}""");

        var obj = Assert.IsType<JsonObject>(JsonNode.Parse(sent!));
        Assert.Equal("enabled", obj["thinking"]?["type"]?.GetValue<string>());
    }
}
