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

    public IChatClient Create(AiModel model)
    {
        var isLocal = model.Endpoint.Contains("localhost")
            || model.Endpoint.Contains("127.0.0.1")
            || model.Endpoint.Contains("::1"); // IPv6 loopback, incl. the [::1] URL form
        var http = _httpClientFactory.CreateClient(isLocal ? DirectClientName : ProxiedClientName);

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(model.Endpoint.TrimEnd('/')),
            // Route the SDK through the named HttpClient: it owns the handler pipeline (proxy choice +
            // the thinking-disable shim) and an infinite timeout. LlmClient enforces the single per-call
            // deadline, so disable the SDK's own 100s network timeout here.
            Transport = new HttpClientPipelineTransport(http),
            NetworkTimeout = Timeout.InfiniteTimeSpan,
        };

        var client = new OpenAIClient(new ApiKeyCredential(model.ApiKey), options);
        return client.GetChatClient(model.ModelId).AsIChatClient();
    }
}
