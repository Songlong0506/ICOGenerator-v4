using System.Runtime.CompilerServices;
using ICOGenerator.Domain;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Locks the text-only-endpoint fallback: a model flagged SupportsVision whose API rejects image parts
// (DeepSeek's 400 "unknown variant `image_url`, expected `text`") gets ONE retry without the images,
// so the BA turn survives on the text context instead of surfacing an API-error turn.
public class LlmClientTests
{
    private static AiModel Model(bool supportsStructuredOutput = false) =>
        new() { ModelId = "deepseek-v4-flash", Endpoint = "https://api.deepseek.com", SupportsStructuredOutput = supportsStructuredOutput };
    private static ModelCallLogContext Ctx() => new(Guid.NewGuid(), new Agent(), "TestPurpose");

    private const string ImageRejectedError =
        "HTTP 400 (invalid_request_error: invalid_request_error)\r\n\r\nFailed to deserialize the JSON body into the target type: messages[22]: unknown variant `image_url`, expected `text` at line 1 column 185749";

    private static List<ChatMessage> MessagesWithImage() => new()
    {
        new ChatMessage(ChatRole.System, "system prompt"),
        new ChatMessage(ChatRole.User, new List<AIContent>
        {
            new TextContent("user turn with attachment"),
            new DataContent(new byte[] { 1, 2, 3 }, "image/png")
        })
    };

    private static LlmClient Client(FakeChatClientFactory factory) => new(
        factory,
        new FakeModelCallLogger(),
        new StructuredOutputPolicy(),
        new NoopBudgetGuard(),
        new ConfigurationBuilder().Build(),
        NullLogger<LlmClient>.Instance);

    [Fact]
    public async Task ChatWithLog_RetriesWithoutImages_WhenEndpointRejectsImageContent()
    {
        var factory = new FakeChatClientFactory(rejectImagesWith: ImageRejectedError, replyText: "ok");
        var client = Client(factory);

        var result = await client.ChatWithLogAsync(Model(), MessagesWithImage(), 0.3, Ctx());

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", result.Content);
        Assert.Equal(2, factory.Calls.Count);
        // Retry keeps the text parts but no image parts.
        var retried = factory.Calls[1];
        Assert.DoesNotContain(retried.SelectMany(m => m.Contents), c => c is DataContent);
        Assert.Contains(retried.SelectMany(m => m.Contents).OfType<TextContent>(), t => t.Text.Contains("user turn with attachment"));
    }

    [Fact]
    public async Task ChatWithLog_DoesNotRetry_WhenNoImagesWereSent()
    {
        var factory = new FakeChatClientFactory(alwaysFailWith: ImageRejectedError);
        var client = Client(factory);

        var result = await client.ChatWithLogAsync(Model(), new List<ChatMessage> { new(ChatRole.User, "text only") }, 0.3, Ctx());

        Assert.False(result.IsSuccess);
        Assert.Single(factory.Calls);
    }

    [Fact]
    public async Task ChatWithLog_DoesNotRetry_OnUnrelatedFailure()
    {
        var factory = new FakeChatClientFactory(alwaysFailWith: "HTTP 500 something else broke");
        var client = Client(factory);

        var result = await client.ChatWithLogAsync(Model(), MessagesWithImage(), 0.3, Ctx());

        Assert.False(result.IsSuccess);
        Assert.Single(factory.Calls);
    }

    [Fact]
    public async Task ChatStructured_RetriesWithoutImages_WhenEndpointRejectsImageContent()
    {
        var factory = new FakeChatClientFactory(rejectImagesWith: ImageRejectedError, replyText: """{"answer":"ok"}""");
        var client = Client(factory);

        var (result, value) = await client.ChatStructuredAsync<StructuredReply>(Model(supportsStructuredOutput: true), MessagesWithImage(), 0.3, Ctx());

        Assert.True(result.IsSuccess);
        Assert.NotNull(value);
        Assert.Equal("ok", value!.Answer);
        Assert.Equal(2, factory.Calls.Count);
        Assert.DoesNotContain(factory.Calls[1].SelectMany(m => m.Contents), c => c is DataContent);
    }

    public class StructuredReply
    {
        public string Answer { get; set; } = string.Empty;
    }

    // Factory + inner client that mimic a text-only OpenAI-compatible server: any request carrying an
    // image part fails with the DeepSeek-style 400, text-only requests succeed with replyText.
    private sealed class FakeChatClientFactory : IChatClientFactory
    {
        private readonly string? _rejectImagesWith;
        private readonly string? _alwaysFailWith;
        private readonly string _replyText;

        public List<List<ChatMessage>> Calls { get; } = new();

        public FakeChatClientFactory(string? rejectImagesWith = null, string? alwaysFailWith = null, string replyText = "ok")
        {
            _rejectImagesWith = rejectImagesWith;
            _alwaysFailWith = alwaysFailWith;
            _replyText = replyText;
        }

        public IChatClient Create(AiModel model) => new FakeChatClient(this);

        private string? FailureFor(List<ChatMessage> messages) =>
            _alwaysFailWith
            ?? (messages.Any(m => m.Contents.Any(c => c is DataContent)) ? _rejectImagesWith : null);

        private sealed class FakeChatClient : IChatClient
        {
            private readonly FakeChatClientFactory _owner;

            public FakeChatClient(FakeChatClientFactory owner) => _owner = owner;

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                var list = messages.ToList();
                _owner.Calls.Add(list);
                var failure = _owner.FailureFor(list);
                if (failure != null)
                    throw new InvalidOperationException(failure);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _owner._replyText)));
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                var list = messages.ToList();
                _owner.Calls.Add(list);
                await Task.CompletedTask;
                var failure = _owner.FailureFor(list);
                if (failure != null)
                    throw new InvalidOperationException(failure);
                yield return new ChatResponseUpdate(ChatRole.Assistant, _owner._replyText);
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose() { }
        }
    }

    private sealed class NoopBudgetGuard : IBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeModelCallLogger : IModelCallLogger
    {
        public Task LogAsync(Guid projectId, Agent agent, LlmCallResult callResult, int step, string purpose, Guid? workflowRunId = null)
            => Task.CompletedTask;
    }
}
