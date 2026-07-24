using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Locks the per-API body patching: the non-standard "thinking": { "type": "disabled" } field turns off
// reasoning on OpenAI-compatible models that honour it (e.g. DeepSeek), while the official OpenAI API 400s
// on unknown parameters AND on a non-default temperature for reasoning models (o-series, gpt-5 family).
public class LlmRequestCompatibilityHandlerTests
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

    private static async Task<JsonObject> SendAsync(string url, string body)
    {
        var capture = new CapturingHandler();
        var handler = new LlmRequestCompatibilityHandler { InnerHandler = capture };
        using var invoker = new HttpMessageInvoker(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        await invoker.SendAsync(request, CancellationToken.None);
        return Assert.IsType<JsonObject>(JsonNode.Parse(capture.CapturedBody!));
    }

    [Theory]
    [InlineData("https://api.deepseek.com/v1/chat/completions")]
    [InlineData("http://localhost:1234/v1/chat/completions")]
    public async Task Injects_Thinking_For_NonOpenAI_Endpoints(string url)
    {
        var obj = await SendAsync(url, """{"model":"deepseek-chat","temperature":0.3,"stream":true}""");

        Assert.Equal("disabled", obj["thinking"]?["type"]?.GetValue<string>());
        Assert.Equal(0.3, obj["temperature"]?.GetValue<double>()); // temperature untouched off-OpenAI
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/chat/completions")]
    [InlineData("https://eastus.api.openai.com/v1/chat/completions")]
    public async Task Skips_Thinking_For_OpenAI_Endpoints(string url)
    {
        var obj = await SendAsync(url, """{"model":"gpt-4o","temperature":0.3,"stream":true}""");

        Assert.False(obj.ContainsKey("thinking"));
    }

    [Theory]
    [InlineData("gpt-5-nano")]
    [InlineData("gpt-5")]
    [InlineData("o1")]
    [InlineData("o3-mini")]
    [InlineData("o4-mini")]
    public async Task Drops_Temperature_For_OpenAI_Reasoning_Models(string model)
    {
        var obj = await SendAsync(
            "https://api.openai.com/v1/chat/completions",
            $$"""{"model":"{{model}}","temperature":0.3,"stream":true}""");

        Assert.False(obj.ContainsKey("temperature"));
        Assert.False(obj.ContainsKey("thinking"));
    }

    [Fact]
    public async Task Keeps_Temperature_For_OpenAI_NonReasoning_Models()
    {
        var obj = await SendAsync(
            "https://api.openai.com/v1/chat/completions",
            """{"model":"gpt-4o","temperature":0.3,"stream":true}""");

        Assert.Equal(0.3, obj["temperature"]?.GetValue<double>());
    }

    [Fact]
    public async Task Leaves_Existing_Thinking_Field_Untouched()
    {
        var obj = await SendAsync(
            "https://api.deepseek.com/v1/chat/completions",
            """{"model":"deepseek-chat","thinking":{"type":"enabled"}}""");

        Assert.Equal("enabled", obj["thinking"]?["type"]?.GetValue<string>());
    }
}
