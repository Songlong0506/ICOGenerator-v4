using System.Runtime.CompilerServices;
using ICOGenerator.Domain;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Locks the cross-cutting behaviour now shared by BOTH execution paths (agent native loop + LlmClient):
// per-call result building, error mapping, token cap, step accounting and single-place DB logging.
public class ModelCallLoggingChatClientTests
{
    private static AiModel Model() => new() { ModelId = "m", Endpoint = "http://localhost" };
    private static ModelCallLogContext Ctx(int firstStep = 1) => new(Guid.NewGuid(), new Agent { Name = "BA" }, "TestPurpose", null, firstStep);
    private static ChatMessage[] Hi() => new[] { new ChatMessage(ChatRole.User, "hi") };

    [Fact]
    public async Task Streaming_Success_BuildsResult_LogsOnce_AndReportsCompleted()
    {
        var inner = new FakeChatClient(streamChunks: new[] { "Hello ", "world" });
        var logger = new FakeModelCallLogger();
        LlmCallResult? completed = null;
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), 600, throwOnFailure: false, onCompleted: r => completed = r);

        var text = "";
        await foreach (var u in client.GetStreamingResponseAsync(Hi()))
            text += u.Text;

        Assert.Equal("Hello world", text);
        Assert.NotNull(completed);
        Assert.True(completed!.IsSuccess);
        Assert.Equal("Hello world", completed.Content);
        Assert.Equal("m", completed.ModelId);
        Assert.Single(logger.Logged);
        Assert.Equal("TestPurpose", logger.Logged[0].Purpose);
        Assert.Equal(1, client.StepCount);
    }

    [Fact]
    public async Task Streaming_Failure_WithThrowOnFailure_Throws_AndLogsFailure()
    {
        var inner = new FakeChatClient(streamError: new InvalidOperationException("boom"));
        var logger = new FakeModelCallLogger();
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), 600, throwOnFailure: true);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(Hi())) { }
        });

        Assert.Single(logger.Logged);
        Assert.False(logger.Logged[0].Result.IsSuccess);
        Assert.Equal("boom", logger.Logged[0].Result.ErrorMessage);
    }

    [Fact]
    public async Task Streaming_Failure_WithoutThrow_Swallows_AndReportsFailureResult()
    {
        var inner = new FakeChatClient(streamError: new InvalidOperationException("boom"));
        var logger = new FakeModelCallLogger();
        LlmCallResult? completed = null;
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), 600, throwOnFailure: false, onCompleted: r => completed = r);

        var count = 0;
        await foreach (var _ in client.GetStreamingResponseAsync(Hi()))
            count++;

        Assert.Equal(0, count); // stream ends without yielding anything
        Assert.NotNull(completed);
        Assert.False(completed!.IsSuccess);
        Assert.Single(logger.Logged);
    }

    [Fact]
    public async Task AppliesTokenCap_ToInnerCallOptions()
    {
        var inner = new FakeChatClient(streamChunks: new[] { "x" });
        var client = new ModelCallLoggingChatClient(inner, Model(), new FakeModelCallLogger(), Ctx(), 600, throwOnFailure: false);

        await foreach (var _ in client.GetStreamingResponseAsync(Hi())) { }

        Assert.NotNull(inner.LastOptions);
        Assert.NotNull(inner.LastOptions!.MaxOutputTokens);
        Assert.True(inner.LastOptions.MaxOutputTokens > 0);
    }

    [Fact]
    public async Task NonStreaming_Success_ReturnsResponse_AndLogs()
    {
        var inner = new FakeChatClient(response: new ChatResponse(new ChatMessage(ChatRole.Assistant, "Typed")));
        var logger = new FakeModelCallLogger();
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), 600, throwOnFailure: false);

        var resp = await client.GetResponseAsync(Hi());

        Assert.Equal("Typed", resp.Text);
        Assert.Single(logger.Logged);
        Assert.True(logger.Logged[0].Result.IsSuccess);
    }

    [Fact]
    public async Task FirstStep_FromContext_IsUsedForLoggingAndStepCount()
    {
        var inner = new FakeChatClient(streamChunks: new[] { "x" });
        var logger = new FakeModelCallLogger();
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(firstStep: 5), 600, throwOnFailure: false);

        await foreach (var _ in client.GetStreamingResponseAsync(Hi())) { }

        Assert.Equal(5, logger.Logged[0].Step);
        Assert.Equal(5, client.StepCount);
    }

    // ── Real token usage: prefer the provider's UsageDetails over the ~4-chars/token estimate ──────────

    [Fact]
    public async Task Streaming_UsesProviderUsage_WhenPresent_NotEstimate()
    {
        var usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 22, TotalTokenCount = 33 };
        var inner = new FakeChatClient(streamChunks: new[] { "Hello world this is a long answer" }, usage: usage);
        var logger = new FakeModelCallLogger();
        LlmCallResult? completed = null;
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), 600, throwOnFailure: false, onCompleted: r => completed = r);

        await foreach (var _ in client.GetStreamingResponseAsync(Hi())) { }

        Assert.Equal(11, completed!.PromptTokens);
        Assert.Equal(22, completed.CompletionTokens);
        Assert.Equal(33, completed.TotalTokens);
        Assert.Equal(11, logger.Logged[0].Result.PromptTokens);
    }

    [Fact]
    public async Task NonStreaming_UsesProviderUsage_WhenPresent()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Typed"))
        {
            Usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 9, TotalTokenCount = 16 }
        };
        var inner = new FakeChatClient(response: response);
        var logger = new FakeModelCallLogger();
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), 600, throwOnFailure: false);

        await client.GetResponseAsync(Hi());

        var r = logger.Logged[0].Result;
        Assert.Equal(7, r.PromptTokens);
        Assert.Equal(9, r.CompletionTokens);
        Assert.Equal(16, r.TotalTokens);
    }

    [Fact]
    public async Task Streaming_FallsBackToEstimate_WhenProviderOmitsUsage()
    {
        var inner = new FakeChatClient(streamChunks: new[] { "abcd" }); // no usage → estimate from text
        var logger = new FakeModelCallLogger();
        LlmCallResult? completed = null;
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), 600, throwOnFailure: false, onCompleted: r => completed = r);

        await foreach (var _ in client.GetStreamingResponseAsync(Hi())) { }

        Assert.Equal(TokenEstimator.Estimate("abcd"), completed!.CompletionTokens);
        Assert.True(completed.TotalTokens > 0);
    }

    // ── Budget circuit breaker: refuse BEFORE the round-trip and before any logging ─────────────────────

    [Fact]
    public async Task Streaming_OverBudget_Throws_BeforeCallingModelOrLogging()
    {
        var inner = new FakeChatClient(streamChunks: new[] { "x" });
        var logger = new FakeModelCallLogger();
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), 600, throwOnFailure: false,
            budgetGuard: new ThrowingBudgetGuard());

        await Assert.ThrowsAsync<BudgetExceededException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(Hi())) { }
        });

        Assert.Empty(logger.Logged);       // refused before the call is logged
        Assert.Null(inner.LastOptions);    // the inner model client was never reached
        Assert.Equal(0, client.StepCount); // step not consumed
    }

    [Fact]
    public async Task NonStreaming_OverBudget_Throws_BeforeCallingModelOrLogging()
    {
        var inner = new FakeChatClient(response: new ChatResponse(new ChatMessage(ChatRole.Assistant, "x")));
        var logger = new FakeModelCallLogger();
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), 600, throwOnFailure: false,
            budgetGuard: new ThrowingBudgetGuard());

        await Assert.ThrowsAsync<BudgetExceededException>(() => client.GetResponseAsync(Hi()));

        Assert.Empty(logger.Logged);
        Assert.Null(inner.LastOptions);
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly string[]? _streamChunks;
        private readonly Exception? _streamError;
        private readonly ChatResponse? _response;
        private readonly UsageDetails? _usage;

        public ChatOptions? LastOptions { get; private set; }

        public FakeChatClient(string[]? streamChunks = null, Exception? streamError = null, ChatResponse? response = null, UsageDetails? usage = null)
        {
            _streamChunks = streamChunks;
            _streamError = streamError;
            _response = response;
            _usage = usage;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(_response ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            await Task.CompletedTask;
            if (_streamError != null)
                throw _streamError;
            foreach (var chunk in _streamChunks ?? Array.Empty<string>())
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            // Mirror an OpenAI stream that ends with a usage chunk (stream_options.include_usage); ToChatResponse
            // folds this UsageContent into response.Usage so the middleware can read real token counts.
            if (_usage != null)
            {
                var usageUpdate = new ChatResponseUpdate { Role = ChatRole.Assistant };
                usageUpdate.Contents.Add(new UsageContent(_usage));
                yield return usageUpdate;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class ThrowingBudgetGuard : IBudgetGuard
    {
        public Task EnsureWithinBudgetAsync(Guid projectId, CancellationToken cancellationToken = default)
            => throw new BudgetExceededException(BudgetScope.System, spentUsd: 5m, limitUsd: 3m, BudgetPeriod.Monthly);
    }

    private sealed class FakeModelCallLogger : IModelCallLogger
    {
        public List<(int Step, string Purpose, LlmCallResult Result)> Logged { get; } = new();

        public Task LogAsync(Guid projectId, Agent agent, LlmCallResult callResult, int step, string purpose, Guid? workflowRunId = null)
        {
            Logged.Add((step, purpose, callResult));
            return Task.CompletedTask;
        }
    }
}
