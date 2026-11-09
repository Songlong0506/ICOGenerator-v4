using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Locks the per-API body patching: the non-standard "thinking": { "type": "disabled" } field turns off
// reasoning on the DeepSeek API, and goes NOWHERE else — OpenAI, Azure, Vertex AI 400 on unknown parameters.
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

    [Fact]
    public async Task Injects_Thinking_For_DeepSeek_Api()
    {
        var obj = await SendAsync(
            "https://api.deepseek.com/v1/chat/completions", """{"model":"deepseek-chat","temperature":0.3,"stream":true}""");

        Assert.Equal("disabled", obj["thinking"]?["type"]?.GetValue<string>());
        Assert.Equal(0.3, obj["temperature"]?.GetValue<double>()); // temperature untouched off-OpenAI
    }

    // Danh sách CHO PHÉP: endpoint lạ nhận body chuẩn. Vertex AI (BMF #3) và gateway công ty 400 với
    // trường lạ; vLLM (BMF #6) và server local bỏ qua nó nên gửi cũng vô ích.
    [Theory]
    [InlineData("http://localhost:1234/v1/chat/completions")]
    [InlineData("https://aoai-farm.bosch-temp.com/api/google/v1/endpoints/gemini-3.7-flash/openapi/chat/completions")]
    [InlineData("https://aoai-farm.bosch-temp.com/api/providers/bosch/msp/deepseek-v4-flash/v1/chat/completions")]
    public async Task Skips_Thinking_For_Unknown_Endpoints(string url)
    {
        var obj = await SendAsync(url, """{"model":"m","stream":true}""");

        Assert.False(obj.ContainsKey("thinking"));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/chat/completions")]
    [InlineData("https://eastus.api.openai.com/v1/chat/completions")]
    public async Task Skips_Thinking_For_OpenAI_Endpoints(string url)
    {
        var obj = await SendAsync(url, """{"model":"gpt-4o","temperature":0.3,"stream":true}""");

        Assert.False(obj.ContainsKey("thinking"));
    }

    // PROMPT CACHE. Token đọc từ prefix cache rẻ hơn token input đầy đủ 10 lần, và prompt nền của BA chat
    // (~19.000 token) đi lại NGUYÊN SI mỗi lượt — nên hai trường này là khoản tiết kiệm lớn
    // nhất của app. Mặc định của OpenAI chỉ giữ cache 5–10 phút, ngắn hơn khoảng nghỉ thường thấy giữa
    // hai lượt phỏng vấn BA, nên retention phải được nói ra chứ không thể để mặc định.
    [Theory]
    [InlineData("https://api.openai.com/v1/chat/completions")]
    [InlineData("https://eastus.api.openai.com/v1/chat/completions")]
    public async Task Adds_PromptCache_Fields_For_OpenAI_Endpoints(string url)
    {
        LlmCacheScope.CacheKey = "ico-proj-abc";
        try
        {
            var obj = await SendAsync(url, """{"model":"gpt-5.6-luna","stream":true}""");

            Assert.Equal(OpenAiCompatibility.PromptCacheRetention, obj["prompt_cache_retention"]?.GetValue<string>());
            Assert.Equal("ico-proj-abc", obj["prompt_cache_key"]?.GetValue<string>());
        }
        finally
        {
            LlmCacheScope.CacheKey = null;
        }
    }

    // Endpoint chỉ TƯƠNG THÍCH OpenAI (DeepSeek, server local) 400 với tham số lạ — hai trường cache là
    // của riêng OpenAI thật, gửi sang chỗ khác là làm hỏng lời gọi.
    [Theory]
    [InlineData("https://api.deepseek.com/v1/chat/completions")]
    [InlineData("http://localhost:1234/v1/chat/completions")]
    public async Task Never_Adds_PromptCache_Fields_Off_OpenAI(string url)
    {
        LlmCacheScope.CacheKey = "ico-proj-abc";
        try
        {
            var obj = await SendAsync(url, """{"model":"deepseek-chat","stream":true}""");

            Assert.False(obj.ContainsKey("prompt_cache_retention"));
            Assert.False(obj.ContainsKey("prompt_cache_key"));
        }
        finally
        {
            LlmCacheScope.CacheKey = null;
        }
    }

    // Không có khóa (đường agent/eval chạy ngoài một project) vẫn phải xin được retention: cache vẫn khớp
    // theo prefix, prompt_cache_key chỉ là gợi ý định tuyến.
    [Fact]
    public async Task WithoutCacheKey_StillAsksForRetention()
    {
        LlmCacheScope.CacheKey = null;
        var obj = await SendAsync("https://api.openai.com/v1/chat/completions", """{"model":"gpt-5.6-luna"}""");

        Assert.Equal(OpenAiCompatibility.PromptCacheRetention, obj["prompt_cache_retention"]?.GetValue<string>());
        Assert.False(obj.ContainsKey("prompt_cache_key"));
    }

    // Azure OpenAI và gateway kiểu Azure (Bosch Model Farm) 400 "Unknown parameter: 'thinking'" — và cả
    // hai trường cache cũng không chắc api-version nào nhận, nên body phải đi nguyên.
    [Theory]
    [InlineData("https://aoai-farm.bosch-temp.com/api/openai/deployments/gpt-6-luna-2026-09-22/chat/completions?api-version=2025-04-01-preview")]
    [InlineData("https://myres.openai.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21")]
    public async Task Adds_Nothing_For_AzureStyle_Endpoints(string url)
    {
        LlmCacheScope.CacheKey = "ico-proj-abc";
        try
        {
            var obj = await SendAsync(url, """{"model":"gpt-6-luna-2026-09-22","stream":true}""");

            Assert.False(obj.ContainsKey("thinking"));
            Assert.False(obj.ContainsKey("prompt_cache_retention"));
            Assert.False(obj.ContainsKey("prompt_cache_key"));
        }
        finally
        {
            LlmCacheScope.CacheKey = null;
        }
    }

    // Call log phải soi gương handler — nếu không, người mở log để hỏi "vì sao 400 thinking" sẽ thấy sai.
    [Theory]
    [InlineData("https://aoai-farm.bosch-temp.com/api/openai/deployments/gpt-6-luna-2026-09-22?api-version=2025-04-01-preview", false)]
    [InlineData("https://aoai-farm.bosch-temp.com/api/google/v1/endpoints/gemini-3.7-flash/openapi", false)]
    [InlineData("http://127.0.0.1:1234/v1", false)]
    [InlineData("https://api.deepseek.com", true)]
    public void RequestPreview_Mirrors_Thinking_Rule(string endpoint, bool expectThinking)
    {
        var model = new AiModel { ModelId = "m", Endpoint = endpoint };
        var json = ModelCallRequestPreview.Build(
            model, new List<ChatMessage> { new(ChatRole.User, "ping") }, new ChatOptions(), 16, streaming: false);

        var obj = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.Equal(expectThinking, obj.ContainsKey("thinking"));
    }

    [Fact]
    public async Task Leaves_Existing_Thinking_Field_Untouched()
    {
        var obj = await SendAsync(
            "https://api.deepseek.com/v1/chat/completions",
            """{"model":"deepseek-chat","thinking":{"type":"enabled"}}""");

        Assert.Equal("enabled", obj["thinking"]?["type"]?.GetValue<string>());
    }

    // TRẦN OUTPUT. SDK OpenAI 2.x gửi max_completion_tokens; DeepSeek (gốc lẫn qua gateway) chỉ đọc max_tokens
    // và lặng lẽ bỏ qua tên kia — ca thật: mọi lượt PocVariant của deepseek-v4-flash cụt đúng 8.192 token
    // (trần mặc định của server) dù app xin 16.000. Endpoint OpenAI-compatible khác cũng đọc max_tokens.
    [Theory]
    [InlineData("https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://aoai-farm.bosch-temp.com/api/providers/bosch/msp/deepseek-v4-flash/v1/chat/completions")]
    [InlineData("http://localhost:1234/v1/chat/completions")]
    public async Task Renames_MaxCompletionTokens_To_MaxTokens_For_Compatible_Endpoints(string url)
    {
        var obj = await SendAsync(url, """{"model":"m","max_completion_tokens":16000,"stream":true}""");

        Assert.Equal(16000, obj["max_tokens"]?.GetValue<int>());
        Assert.False(obj.ContainsKey("max_completion_tokens"));
    }

    // OpenAI thật và kiểu Azure hiểu max_completion_tokens, còn model reasoning của chúng 400 với max_tokens.
    [Theory]
    [InlineData("https://api.openai.com/v1/chat/completions")]
    [InlineData("https://myres.openai.azure.com/openai/deployments/gpt/chat/completions?api-version=2025-04-01-preview")]
    [InlineData("https://aoai-farm.bosch-temp.com/api/openai/deployments/gpt-6-luna-2026-09-22/chat/completions?api-version=2025-04-01-preview")]
    public async Task Keeps_MaxCompletionTokens_For_OpenAI_And_Azure(string url)
    {
        var obj = await SendAsync(url, """{"model":"m","max_completion_tokens":16000}""");

        Assert.Equal(16000, obj["max_completion_tokens"]?.GetValue<int>());
        Assert.False(obj.ContainsKey("max_tokens"));
    }

    // Call log phải ghi đúng tên trường đã thực sự đi ra — nó từng luôn ghi "max_tokens" nên che mất lỗi trên.
    [Theory]
    [InlineData("https://api.deepseek.com/v1", "max_tokens")]
    [InlineData("https://api.openai.com/v1", "max_completion_tokens")]
    [InlineData("https://aoai-farm.bosch-temp.com/api/openai/deployments/gpt-6-luna-2026-09-22?api-version=2025-04-01-preview", "max_completion_tokens")]
    public void Preview_Shows_The_Output_Cap_Field_Actually_Sent(string endpoint, string expectedKey)
    {
        var model = new AiModel { ModelId = "m", Endpoint = endpoint };
        var json = ModelCallRequestPreview.Build(
            model, new List<ChatMessage> { new(ChatRole.User, "ping") }, new ChatOptions(), 16, streaming: false);

        var obj = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.Equal(16, obj[expectedKey]?.GetValue<int>());
        Assert.Single(obj.Where(p => p.Key is "max_tokens" or "max_completion_tokens"));
    }

    // Model reasoning (vd gpt-6-luna) trên /v1/chat/completions 400 với MỌI request có tool khi mức suy luận
    // khác 'none' — agent loop không chạy được bước nào. Gửi lại một lần với reasoning_effort=none, và từ đó
    // gửi sẵn cho cùng model để khỏi tốn một lượt 400 mỗi bước.
    private sealed class ReasoningRejectingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            if (body.Contains("\"reasoning_effort\":\"none\""))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"Function tools with reasoning_effort are not supported for gpt-6-luna in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'.","param":"reasoning_effort"}}""")
            };
        }
    }

    private static async Task<HttpResponseMessage> SendThrough(ReasoningRejectingHandler inner, string url, string body)
    {
        using var invoker = new HttpMessageInvoker(new LlmRequestCompatibilityHandler { InnerHandler = inner }, disposeHandler: false);
        return await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ARequestWithTools_RejectedForReasoningEffort_IsResentWithReasoningOff_AndRemembered()
    {
        var url = $"https://gw-{Guid.NewGuid():N}.example/v1/chat/completions";
        var withTools = """{"model":"gpt-6-luna","tools":[{"type":"function","function":{"name":"f"}}]}""";
        var inner = new ReasoningRejectingHandler();

        var first = await SendThrough(inner, url, withTools);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(2, inner.Bodies.Count);
        Assert.Equal("none", JsonNode.Parse(inner.Bodies[1])!["reasoning_effort"]!.GetValue<string>());

        // Lượt sau của cùng model: gửi sẵn, không còn lượt 400.
        var second = await SendThrough(inner, url, withTools);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(3, inner.Bodies.Count);
        Assert.Contains("\"reasoning_effort\":\"none\"", inner.Bodies[2]);
    }

    // Không có tool thì không đụng: lượt thường vẫn được suy luận ẩn như cũ, và lỗi 400 trả nguyên cho caller.
    [Fact]
    public async Task ARequestWithoutTools_IsNeverResent()
    {
        var inner = new ReasoningRejectingHandler();

        var response = await SendThrough(inner, $"https://gw-{Guid.NewGuid():N}.example/v1/chat/completions", """{"model":"gpt-6-luna"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(inner.Bodies);
        Assert.Contains("reasoning_effort", await response.Content.ReadAsStringAsync());
    }
}
