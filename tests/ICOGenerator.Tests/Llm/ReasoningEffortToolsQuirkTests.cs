using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// gpt-6-luna trên /v1/chat/completions (Bosch Model Farm) 400 khi lượt có function tools mà model đang để
// reasoning_effort mặc định: "Function tools with reasoning_effort are not supported … set reasoning_effort
// to 'none'". Handler phải gửi lại với "none", nhớ cặp (host, model), và KHÔNG đụng tới model khác.
// Bộ nhớ quirk là tĩnh trong tiến trình ⇒ mỗi test dùng model id riêng để không dính nhau.
public class ReasoningEffortToolsQuirkTests
{
    private const string LunaError = """
        {"error":{"message":"Function tools with reasoning_effort are not supported for gpt-6-luna-2026-09-22 in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'.","type":"invalid_request_error","param":"reasoning_effort","code":null}}
        """;

    // Từ chối mọi body có tools mà chưa xin reasoning_effort "none"; ghi lại từng body nhận được.
    private sealed class LunaLikeServer(string rejection) : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var obj = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Bodies.Add(obj);
            var hasTools = obj["tools"] is JsonArray { Count: > 0 };
            var off = obj["reasoning_effort"]?.GetValue<string>() == "none";
            return hasTools && !off
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(rejection, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private const string AzureUrl =
        "https://aoai-farm.bosch-temp.com/api/openai/deployments/{0}/chat/completions?api-version=2025-04-01-preview";

    private static async Task<HttpResponseMessage> SendAsync(HttpMessageHandler server, string url, string body)
    {
        using var invoker = new HttpMessageInvoker(new LlmRequestCompatibilityHandler { InnerHandler = server });
        return await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }, CancellationToken.None);
    }

    private static string Body(string model, bool tools) => tools
        ? $$$"""{"model":"{{{model}}}","messages":[],"tools":[{"type":"function","function":{"name":"read_file"}}]}"""
        : $$"""{"model":"{{model}}","messages":[]}""";

    [Fact]
    public async Task Retries_With_ReasoningOff_And_Learns_The_Model()
    {
        const string model = "luna-retry";
        var server = new LunaLikeServer(LunaError);
        var url = string.Format(AzureUrl, model);

        var first = await SendAsync(server, url, Body(model, tools: true));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(2, server.Bodies.Count);
        Assert.False(server.Bodies[0].ContainsKey("reasoning_effort"));
        Assert.Equal("none", server.Bodies[1]["reasoning_effort"]?.GetValue<string>());
        Assert.False(server.Bodies[1].ContainsKey("thinking")); // Azure-style: vẫn không thêm trường lạ

        // Lượt sau: gửi "none" ngay từ đầu, không tốn thêm một lần 400.
        var second = await SendAsync(server, url, Body(model, tools: true));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(3, server.Bodies.Count);
        Assert.Equal("none", server.Bodies[2]["reasoning_effort"]?.GetValue<string>());
    }

    [Fact]
    public async Task Turn_Without_Tools_Never_Gets_ReasoningEffort()
    {
        const string model = "luna-no-tools";
        var server = new LunaLikeServer(LunaError);
        var url = string.Format(AzureUrl, model);
        await SendAsync(server, url, Body(model, tools: true)); // học quirk

        await SendAsync(server, url, Body(model, tools: false));

        Assert.False(server.Bodies[^1].ContainsKey("reasoning_effort"));
    }

    // 400 vì lý do khác thì trả nguyên lỗi lên, body lỗi vẫn đọc được, không gửi lại, không học gì.
    [Fact]
    public async Task Other_400_Passes_Through_Untouched()
    {
        const string model = "other-400";
        var server = new LunaLikeServer("""{"error":{"message":"context_length_exceeded"}}""");

        var response = await SendAsync(server, string.Format(AzureUrl, model), Body(model, tools: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(server.Bodies);
        Assert.Contains("context_length_exceeded", await response.Content.ReadAsStringAsync());
        Assert.False(OpenAiCompatibility.RequiresReasoningOffWithTools("aoai-farm.bosch-temp.com", model));
    }

    [Fact]
    public async Task RequestPreview_Shows_Learned_ReasoningOff_Only_With_Tools()
    {
        const string model = "luna-preview";
        await SendAsync(new LunaLikeServer(LunaError), string.Format(AzureUrl, model), Body(model, tools: true));
        var aiModel = new AiModel
        {
            ModelId = model,
            Endpoint = $"https://aoai-farm.bosch-temp.com/api/openai/deployments/{model}?api-version=2025-04-01-preview"
        };
        var messages = new List<ChatMessage> { new(ChatRole.User, "ping") };

        var withTools = JsonNode.Parse(ModelCallRequestPreview.Build(aiModel, messages,
            new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "read_file")] }, 16, streaming: true))!;
        var withoutTools = JsonNode.Parse(ModelCallRequestPreview.Build(aiModel, messages, new ChatOptions(), 16, streaming: true))!;

        Assert.Equal("none", withTools["reasoning_effort"]?.GetValue<string>());
        Assert.Null(withoutTools["reasoning_effort"]);
    }
}
