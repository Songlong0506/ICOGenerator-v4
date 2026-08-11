using System.ClientModel;
using System.ClientModel.Primitives;
using ICOGenerator.Domain;
using Microsoft.Extensions.AI;
using OpenAI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Creates an OpenAI-compatible <see cref="IChatClient"/> per <see cref="AiModel"/>. Endpoint, model id
/// and API key live in the DB and are edited in the UI, so they vary per call — a lightweight OpenAI
/// client is built each time. The expensive resource (the pooled <see cref="HttpMessageHandler"/>) is
/// still shared via <see cref="IHttpClientFactory"/>, keyed by whether the endpoint is local (direct)
/// or remote (proxied), preserving the previous proxy behaviour.
/// </summary>
public class OpenAIChatClientFactory : IChatClientFactory
{
    // Named handler pools registered in DI; the proxy choice is baked into each handler.
    public const string DirectClientName = "llm-direct";
    public const string ProxiedClientName = "llm-proxied";

    private readonly IHttpClientFactory _httpClientFactory;

    public OpenAIChatClientFactory(IHttpClientFactory httpClientFactory)
        => _httpClientFactory = httpClientFactory;

    /// <summary>
    /// True khi endpoint trỏ về chính máy đang chạy app ⇒ đi thẳng, không qua proxy. Là chỗ DUY NHẤT
    /// định nghĩa "local" cho lời gọi LLM: <see cref="ModelConnectionTester"/> phải trả lời được câu
    /// "lời gọi vừa rồi có đi qua proxy không" để chỉ đúng chỗ hỏng, và nếu nó tự chép lại luật này thì
    /// hai bên sẽ lệch nhau ngay lần đầu ai đó thêm một dạng địa chỉ loopback.
    /// </summary>
    public static bool IsLocalEndpoint(string? endpoint) =>
        endpoint is not null
        && (endpoint.Contains("localhost")
            || endpoint.Contains("127.0.0.1")
            || endpoint.Contains("::1")); // IPv6 loopback, incl. the [::1] URL form

    public IChatClient Create(AiModel model)
    {
        var http = _httpClientFactory.CreateClient(IsLocalEndpoint(model.Endpoint) ? DirectClientName : ProxiedClientName);

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(model.Endpoint.TrimEnd('/')),
            // Route the SDK through the named HttpClient: it owns the handler pipeline (proxy choice +
            // the per-API request-compatibility shim) and an infinite timeout. LlmClient enforces the single per-call
            // deadline, so disable the SDK's own 100s network timeout here.
            Transport = new HttpClientPipelineTransport(http),
            NetworkTimeout = Timeout.InfiniteTimeSpan,
        };

        var client = new OpenAIClient(new ApiKeyCredential(model.ApiKey), options);
        return client.GetChatClient(model.ModelId).AsIChatClient();
    }
}
