using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ICOGenerator.Services.Llm;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Ca thật: Developer gắn gpt-6-luna-2026-09-22 ⇒ bước đầu tiên 400 "Function tools with reasoning_effort are not
// supported … set reasoning_effort to 'none'", dù app không hề gửi reasoning_effort (mặc định của model). Chat BA
// không khai tool nên vẫn chạy. Handler phải làm đúng điều lỗi bảo, chỉ cho endpoint đã nói ra điều đó.
public class ReasoningOffRetryTests
{
    private const string Rejection = """
        {"error":{"message":"Function tools with reasoning_effort are not supported for gpt-6-luna-2026-09-22 in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'.","type":"invalid_request_error","param":"reasoning_effort"}}
        """;

    // Server giả: từ chối mọi request có tool mà chưa tắt suy luận, ghi lại từng body nhận được.
    private sealed class ReasoningModel : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Bodies.Add(body);
            var rejected = body["tools"] is JsonArray { Count: > 0 } && body["reasoning_effort"]?.ToString() != "none";
            return rejected
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(Rejection, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<HttpStatusCode> SendAsync(ReasoningModel server, string url, string body)
    {
        using var invoker = new HttpMessageInvoker(new LlmRequestCompatibilityHandler { InnerHandler = server }, disposeHandler: false);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("api-key", "k");
        using var response = await invoker.SendAsync(request, CancellationToken.None);
        return response.StatusCode;
    }

    private const string WithTools = """{"model":"gpt-6-luna-2026-09-22","tools":[{"type":"function","function":{"name":"f"}}],"stream":true}""";

    [Fact]
    public async Task ARejectedToolCall_IsResentWithReasoningOff_AndRemembered()
    {
        var server = new ReasoningModel();
        var url = $"https://farm.example/{Guid.NewGuid():N}/v1/chat/completions";

        Assert.Equal(HttpStatusCode.OK, await SendAsync(server, url, WithTools));
        Assert.Equal(2, server.Bodies.Count);
        Assert.False(server.Bodies[0].ContainsKey("reasoning_effort"));
        Assert.Equal("none", server.Bodies[1]["reasoning_effort"]!.ToString());

        // Lượt sau của cùng endpoint + model: gửi "none" ngay, không tốn thêm một lần 400.
        Assert.Equal(HttpStatusCode.OK, await SendAsync(server, url, WithTools));
        Assert.Equal(3, server.Bodies.Count);
        Assert.Equal("none", server.Bodies[2]["reasoning_effort"]!.ToString());
    }

    // Không tool thì không đụng: chat BA và model không reasoning không bao giờ thấy tham số lạ.
    [Fact]
    public async Task ARequestWithoutTools_IsNeverTouched()
    {
        var server = new ReasoningModel();
        var url = $"https://farm.example/{Guid.NewGuid():N}/v1/chat/completions";

        await SendAsync(server, url, """{"model":"gpt-6-luna-2026-09-22","stream":true}""");

        Assert.False(Assert.Single(server.Bodies).ContainsKey("reasoning_effort"));
    }

    // 400 vì lý do khác thì trả nguyên lỗi, không gửi lại.
    [Fact]
    public void OnlyTheReasoningRejection_TriggersTheRetry()
    {
        Assert.True(LlmRequestCompatibilityHandler.NeedsReasoningOff(Rejection));
        Assert.False(LlmRequestCompatibilityHandler.NeedsReasoningOff("""{"error":{"message":"Unsupported parameter: 'reasoning_effort'"}}"""));
        Assert.False(LlmRequestCompatibilityHandler.NeedsReasoningOff("""{"error":{"message":"context_length_exceeded"}}"""));
    }
}
