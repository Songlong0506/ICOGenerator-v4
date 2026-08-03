using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Llm;

/// <summary>
/// The other LlmClient tests stub out <see cref="IChatClient"/>, so they pin our own branching but say nothing
/// about the bytes that leave the process — and "which response_format did we actually send" is the entire
/// question behind <see cref="StructuredOutputMode"/> (DeepSeek 400s on one spelling and not the other).
/// These drive the REAL OpenAI SDK client against a loopback stub and read the request body off the wire.
/// </summary>
public class StructuredOutputWireFormatTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private string _endpoint = null!;
    private Task _serverLoop = null!;

    /// <summary>Bodies received, in order.</summary>
    private readonly List<JsonObject> _requests = new();

    /// <summary>When set, the next request is answered with this HTTP 400 body instead of a completion.</summary>
    private string? _rejectWith;

    public Task InitializeAsync()
    {
        // Port 0 → the OS picks a free one, so parallel test runs can't collide.
        var port = FreePort();
        _endpoint = $"http://127.0.0.1:{port}/v1";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _serverLoop = Task.Run(ServeAsync);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _listener.Stop();
        return Task.CompletedTask;
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { return; }   // listener stopped
            catch (ObjectDisposedException) { return; }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var request = JsonNode.Parse(body)!.AsObject();
            lock (_requests)
                _requests.Add(request);

            if (_rejectWith is { } error)
            {
                _rejectWith = null;   // reject once, then behave — mirrors "retry without response_format"
                await WriteAsync(context, 400, "application/json", error);
                continue;
            }

            // The json_schema level is a single round-trip, the others stream — answer in the shape the SDK
            // asked for, or it fails to read our reply and the test measures the stub instead of the code.
            await (request["stream"]?.GetValue<bool>() == true
                ? WriteAsync(context, 200, "text/event-stream",
                    """
                    data: {"id":"1","object":"chat.completion.chunk","created":0,"model":"stub","choices":[{"index":0,"delta":{"role":"assistant","content":"{\"answer\":\"ok\"}"},"finish_reason":null}]}

                    data: {"id":"1","object":"chat.completion.chunk","created":0,"model":"stub","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                    data: [DONE]


                    """)
                : WriteAsync(context, 200, "application/json",
                    """
                    {"id":"1","object":"chat.completion","created":0,"model":"stub","choices":[{"index":0,"message":{"role":"assistant","content":"{\"answer\":\"ok\"}"},"finish_reason":"stop"}]}
                    """));
        }
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private LlmClient Client()
    {
        // Real factory + real handler pipeline (incl. LlmRequestCompatibilityHandler), so the body under test
        // is the one production sends.
        var services = new ServiceCollection();
        services.AddHttpClient(OpenAIChatClientFactory.DirectClientName)
            .AddHttpMessageHandler(() => new LlmRequestCompatibilityHandler());
        var httpClientFactory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        return new LlmClient(
            new OpenAIChatClientFactory(httpClientFactory),
            new NullModelCallLogger(),
            new NoBudgetLimit(),
            new LlmSettings(),
            NullLogger<LlmClient>.Instance);
    }

    private AiModel Model(StructuredOutputMode mode) =>
        new() { ModelId = "stub-model", Endpoint = _endpoint, ApiKey = "sk-stub", StructuredOutputMode = mode };

    private static List<ChatMessage> Messages() => new() { new(ChatRole.User, "trả lời bằng json") };

    private static ModelCallLogContext Ctx() => new(Guid.NewGuid(), new Agent(), "WireFormatTest");

    private JsonObject Request(int index)
    {
        lock (_requests)
            return _requests[index];
    }

    private int RequestCount()
    {
        lock (_requests)
            return _requests.Count;
    }

    [Fact]
    public async Task None_SendsNoResponseFormat()
    {
        var (result, _) = await Client().ChatStructuredAsync<Reply>(Model(StructuredOutputMode.None), Messages(), 0.3, Ctx());

        Assert.True(result.IsSuccess);
        Assert.False(Request(0).ContainsKey("response_format"));
    }

    // The claim this whole change rests on: JsonObject must reach the wire as the json_object spelling
    // DeepSeek accepts, on a streaming request.
    [Fact]
    public async Task JsonObject_SendsJsonObjectResponseFormat_OnAStreamingRequest()
    {
        var (result, value) = await Client().ChatStructuredAsync<Reply>(Model(StructuredOutputMode.JsonObject), Messages(), 0.3, Ctx());

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", value?.Answer);

        var request = Request(0);
        Assert.Equal("json_object", request["response_format"]!["type"]!.GetValue<string>());
        Assert.True(request["stream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task JsonSchema_SendsJsonSchemaResponseFormat()
    {
        var (result, _) = await Client().ChatStructuredAsync<Reply>(Model(StructuredOutputMode.JsonSchema), Messages(), 0.3, Ctx());

        var request = Request(0);
        Assert.Equal("json_schema", request["response_format"]!["type"]!.GetValue<string>());
        Assert.False(request["stream"]?.GetValue<bool>() ?? false);
        Assert.True(result.IsSuccess);
    }

    // End to end over HTTP: the exact DeepSeek 400 must cost one wasted call and then be retried clean,
    // instead of taking the caller's feature down with it.
    [Fact]
    public async Task ResponseFormatRejected_IsRetriedWithoutResponseFormat()
    {
        _rejectWith = """{"error":{"message":"This response_format type is unavailable now","type":"invalid_request_error"}}""";

        var (result, value) = await Client().ChatStructuredAsync<Reply>(Model(StructuredOutputMode.JsonObject), Messages(), 0.3, Ctx());

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", value?.Answer);
        Assert.Equal(2, RequestCount());
        Assert.True(Request(0).ContainsKey("response_format"));
        Assert.False(Request(1).ContainsKey("response_format"));
    }

    public class Reply
    {
        public string Answer { get; set; } = string.Empty;
    }

    private sealed class NullModelCallLogger : IModelCallLogger
    {
        public Task LogAsync(Guid projectId, Agent agent, LlmCallResult callResult, int step, string purpose, Guid? workflowRunId = null)
            => Task.CompletedTask;
    }

    private sealed class NoBudgetLimit : IBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
