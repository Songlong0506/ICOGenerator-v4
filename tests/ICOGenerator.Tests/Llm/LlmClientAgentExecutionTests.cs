using System.Runtime.CompilerServices;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Locks the BA execution path now that LlmClient drives a Microsoft Agent Framework ChatClientAgent
// (instead of calling IChatClient directly): the shared logging middleware must still fire, streaming must
// still surface tokens, structured output must deserialize when opted in, and every failure mode must keep
// falling back to the manual-parser/text path so weak local models never break.
public class LlmClientAgentExecutionTests
{
    private const string StructuredModelId = "structured-model";

    private static AiModel Model(string modelId = "m") =>
        new() { Name = "M", ModelId = modelId, Endpoint = "http://localhost", ApiKey = "k" };

    private static ModelCallLogContext Ctx() => new(Guid.NewGuid(), new Agent { Name = "BA" }, "BAChat");

    private static LlmClient Build(IChatClient inner, FakeModelCallLogger logger, bool structuredEnabled)
    {
        var factory = new FakeChatClientFactory(inner);
        var policy = new StructuredOutputPolicy(structuredEnabled, new[] { StructuredModelId });
        var config = new ConfigurationBuilder().Build();
        return new LlmClient(factory, logger, policy, config, NullLogger<LlmClient>.Instance);
    }

    [Fact]
    public async Task ChatWithLog_StreamsTokens_BuildsResult_AndLogsOnce()
    {
        var logger = new FakeModelCallLogger();
        var client = Build(new FakeChatClient(streamChunks: new[] { "Hel", "lo" }), logger, structuredEnabled: false);

        var streamed = "";
        var result = await client.ChatWithLogAsync(
            Model(), new List<ChatMessage> { new(ChatRole.System, "sys"), new(ChatRole.User, "hi") },
            0.5, Ctx(), onToken: t => streamed += t);

        Assert.Equal("Hello", streamed);
        Assert.True(result.IsSuccess);
        Assert.Equal("Hello", result.Content);
        Assert.Single(logger.Logged);
        Assert.Equal("BAChat", logger.Logged[0].Purpose);
    }

    [Fact]
    public async Task ChatStructured_OptedIn_ReturnsTypedValue()
    {
        var logger = new FakeModelCallLogger();
        var json = "{\"message\":\"need a name\",\"suggestions\":[\"Shop\",\"Blog\"]}";
        var client = Build(new FakeChatClient(response: json), logger, structuredEnabled: true);

        var (result, value) = await client.ChatStructuredAsync<BAChatReply>(
            Model(StructuredModelId), new List<ChatMessage> { new(ChatRole.User, "hi") }, 0.3, Ctx());

        Assert.True(result.IsSuccess);
        Assert.NotNull(value);
        Assert.Equal("need a name", value!.Message);
        Assert.Equal(new[] { "Shop", "Blog" }, value.Suggestions);
        Assert.Single(logger.Logged);
    }

    [Fact]
    public async Task ChatStructured_NotOptedIn_FallsBackToTextPath_NullValue()
    {
        var logger = new FakeModelCallLogger();
        // Model not on the allow-list → stays on the streamed text path; caller parses Content itself.
        var client = Build(new FakeChatClient(streamChunks: new[] { "{\"message\":\"hi\"}" }), logger, structuredEnabled: true);

        var (result, value) = await client.ChatStructuredAsync<BAChatReply>(
            Model("not-listed"), new List<ChatMessage> { new(ChatRole.User, "hi") }, 0.3, Ctx());

        Assert.Null(value);
        Assert.True(result.IsSuccess);
        Assert.Equal("{\"message\":\"hi\"}", result.Content);
    }

    [Fact]
    public async Task ChatStructured_OptedIn_ButCallFails_DoesNotThrow_AndReturnsNullValue()
    {
        var logger = new FakeModelCallLogger();
        // Server rejects response_format (typical for weak/local servers): the call fails but the BA flow
        // must keep going with a null value so the caller can degrade gracefully.
        var client = Build(new FakeChatClient(responseError: new InvalidOperationException("response_format unsupported")), logger, structuredEnabled: true);

        var (result, value) = await client.ChatStructuredAsync<BAChatReply>(
            Model(StructuredModelId), new List<ChatMessage> { new(ChatRole.User, "hi") }, 0.3, Ctx());

        Assert.Null(value);
        Assert.False(result.IsSuccess);
    }

    private sealed class FakeChatClientFactory : IChatClientFactory
    {
        private readonly IChatClient _inner;
        public FakeChatClientFactory(IChatClient inner) => _inner = inner;
        public IChatClient Create(AiModel model) => _inner;
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly string[]? _streamChunks;
        private readonly string? _response;
        private readonly Exception? _responseError;

        public FakeChatClient(string[]? streamChunks = null, string? response = null, Exception? responseError = null)
        {
            _streamChunks = streamChunks;
            _response = response;
            _responseError = responseError;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (_responseError != null)
                throw _responseError;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response ?? string.Empty)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var chunk in _streamChunks ?? Array.Empty<string>())
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FakeModelCallLogger : IModelCallLogger
    {
        public List<(string Purpose, LlmCallResult Result)> Logged { get; } = new();

        public Task LogAsync(Guid projectId, Agent agent, LlmCallResult callResult, int step, string purpose, Guid? workflowRunId = null)
        {
            Logged.Add((purpose, callResult));
            return Task.CompletedTask;
        }
    }
}
