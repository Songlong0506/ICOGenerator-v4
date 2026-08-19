using System.Runtime.CompilerServices;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICOGenerator.Tests.Llm;

// Locks the cross-cutting behaviour now shared by BOTH execution paths (agent native loop + LlmClient):
// per-call result building, error mapping, token cap, step accounting and single-place DB logging.
public class ModelCallLoggingChatClientTests
{
    private static AiModel Model() => new() { ModelId = "m", Endpoint = "http://localhost" };
    private static ModelCallLogContext Ctx() => new(Guid.NewGuid(), new Agent(), "TestPurpose");
    private static ChatMessage[] Hi() => new[] { new ChatMessage(ChatRole.User, "hi") };

    private static ModelCallOptions Opts(bool throwOnFailure, Action<LlmCallResult>? onCompleted = null, IBudgetGuard? budgetGuard = null) =>
        new(RequestTimeoutSeconds: 600, throwOnFailure) { OnCompleted = onCompleted, BudgetGuard = budgetGuard };

    [Fact]
    public async Task Streaming_Success_BuildsResult_LogsOnce_AndReportsCompleted()
    {
        var inner = new FakeChatClient(streamChunks: new[] { "Hello ", "world" });
        var logger = new FakeModelCallLogger();
        LlmCallResult? completed = null;
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: false, onCompleted: r => completed = r));

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
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: true));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(Hi())) { }
        });

        Assert.Single(logger.Logged);
        Assert.False(logger.Logged[0].Result.IsSuccess);
        // Mọi thông điệp lỗi phải nói lời gọi vừa đi TỚI ĐÂU: một agent bị gắn nhầm model là ca gây tốn
        // nhiều thời gian nhất ("chỗ này chạy, chỗ kia lỗi") và không thể lần ra nếu lỗi giấu đích đến.
        Assert.Equal("boom (m @ localhost)", logger.Logged[0].Result.ErrorMessage);
    }

    [Fact]
    public async Task Streaming_Failure_WithoutThrow_Swallows_AndReportsFailureResult()
    {
        var inner = new FakeChatClient(streamError: new InvalidOperationException("boom"));
        var logger = new FakeModelCallLogger();
        LlmCallResult? completed = null;
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: false, onCompleted: r => completed = r));

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
        var client = new ModelCallLoggingChatClient(inner, Model(), new FakeModelCallLogger(), Ctx(), Opts(throwOnFailure: false));

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
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: false));

        var resp = await client.GetResponseAsync(Hi());

        Assert.Equal("Typed", resp.Text);
        Assert.Single(logger.Logged);
        Assert.True(logger.Logged[0].Result.IsSuccess);
    }

    // Số bước đếm từ 1 theo INSTANCE và tự tăng mỗi round-trip: đó là thứ làm cho cột Step ở call log có
    // nghĩa với đường agent (một client dùng chung cho cả task) và luôn bằng 1 với lời gọi một-phát-một-lượt.
    [Fact]
    public async Task Step_StartsAtOne_AndIncrementsPerCall()
    {
        var inner = new FakeChatClient(streamChunks: new[] { "x" });
        var logger = new FakeModelCallLogger();
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: false));

        await foreach (var _ in client.GetStreamingResponseAsync(Hi())) { }
        await foreach (var _ in client.GetStreamingResponseAsync(Hi())) { }

        Assert.Equal(new[] { 1, 2 }, logger.Logged.Select(x => x.Step));
        Assert.Equal(2, client.StepCount);
    }

    // ── Real token usage: prefer the provider's UsageDetails over the ~4-chars/token estimate ──────────

    [Fact]
    public async Task Streaming_UsesProviderUsage_WhenPresent_NotEstimate()
    {
        var usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 22, TotalTokenCount = 33 };
        var inner = new FakeChatClient(streamChunks: new[] { "Hello world this is a long answer" }, usage: usage);
        var logger = new FakeModelCallLogger();
        LlmCallResult? completed = null;
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: false, onCompleted: r => completed = r));

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
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: false));

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
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: false, onCompleted: r => completed = r));

        await foreach (var _ in client.GetStreamingResponseAsync(Hi())) { }

        Assert.Equal(TokenEstimator.Estimate("abcd"), completed!.CompletionTokens);
        Assert.True(completed.TotalTokens > 0);
    }

    // ── Cached input tokens: đọc phần prompt provider phục vụ từ cache (rẻ hơn ~10 lần) ────────────────

    [Fact]
    public async Task NonStreaming_ReadsCachedPromptTokens_FromAdditionalCounts()
    {
        var usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 50, TotalTokenCount = 1050, CachedInputTokenCount = 768 };
        var inner = new FakeChatClient(response: new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { Usage = usage });
        var logger = new FakeModelCallLogger();
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: false));

        await client.GetResponseAsync(Hi());

        var r = logger.Logged[0].Result;
        Assert.Equal(768, r.CachedPromptTokens);
        // Phần cache nằm TRONG prompt, không cộng thêm — nếu chỗ nào đó cộng dồn hai cột này thì hỏng ở đây.
        Assert.Equal(1000, r.PromptTokens);
    }

    [Fact]
    public async Task Streaming_ReadsCachedPromptTokens_FromAdditionalCounts()
    {
        var usage = new UsageDetails { InputTokenCount = 400, OutputTokenCount = 10, TotalTokenCount = 410, CachedInputTokenCount = 128 };
        var inner = new FakeChatClient(streamChunks: new[] { "hi" }, usage: usage);
        var logger = new FakeModelCallLogger();
        LlmCallResult? completed = null;
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: false, onCompleted: r => completed = r));

        await foreach (var _ in client.GetStreamingResponseAsync(Hi())) { }

        Assert.Equal(128, completed!.CachedPromptTokens);
        Assert.Equal(128, logger.Logged[0].Result.CachedPromptTokens);
    }

    // Cache là chuyện phía provider: không có số thì để 0 chứ KHÔNG ước lượng — đoán ra một con số là bịa
    // ra một khoản giảm giá không tồn tại, và mọi báo cáo chi phí sẽ thấp hơn hóa đơn thật.
    [Fact]
    public async Task CachedPromptTokens_IsZero_WhenProviderOmitsTheCount()
    {
        var usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 50, TotalTokenCount = 1050 };
        var inner = new FakeChatClient(response: new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { Usage = usage });
        var logger = new FakeModelCallLogger();
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: false));

        await client.GetResponseAsync(Hi());

        Assert.Equal(0, logger.Logged[0].Result.CachedPromptTokens);
    }

    // Endpoint lạ trả cached > prompt sẽ đẩy phần input phải trả tiền xuống ÂM và ăn bớt chi phí của các
    // dòng khác khi cộng dồn — kẹp ngay tại chỗ đọc, đừng để con số vô lý đi tiếp vào DB.
    [Fact]
    public async Task CachedPromptTokens_IsClampedToPromptTokens()
    {
        var usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 5, TotalTokenCount = 105, CachedInputTokenCount = 999_999 };
        var inner = new FakeChatClient(response: new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { Usage = usage });
        var logger = new FakeModelCallLogger();
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: false));

        await client.GetResponseAsync(Hi());

        Assert.Equal(100, logger.Logged[0].Result.CachedPromptTokens);
    }

    // ── Budget circuit breaker: refuse BEFORE the round-trip and before any logging ─────────────────────

    [Fact]
    public async Task Streaming_OverBudget_Throws_BeforeCallingModelOrLogging()
    {
        var inner = new FakeChatClient(streamChunks: new[] { "x" });
        var logger = new FakeModelCallLogger();
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: false,
            budgetGuard: new ThrowingBudgetGuard()));

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
        var client = new ModelCallLoggingChatClient(inner, Model(), logger, Ctx(), Opts(throwOnFailure: false,
            budgetGuard: new ThrowingBudgetGuard()));

        await Assert.ThrowsAsync<BudgetExceededException>(() => client.GetResponseAsync(Hi()));

        Assert.Empty(logger.Logged);
        Assert.Null(inner.LastOptions);
    }

    [Fact]
    public async Task ImagesInRequest_AreCollectedForTheLog_OnlyWhenAnImageStoreIsWired()
    {
        // Ảnh phải được gom TRƯỚC lời gọi và đi cùng LlmCallResult tới chỗ ghi log — kể cả lượt hỏng, vì đó
        // đúng là lượt người ta mở call log ra soi. Không có store (đường agent/eval, vốn không gửi ảnh)
        // thì không gom gì để khỏi copy bytes vô ích.
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, new List<AIContent>
            {
                new TextContent("xem ảnh này"),
                new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = "a.png" },
            })
        };

        LlmCallResult? withStore = null;
        var client = new ModelCallLoggingChatClient(
            new FakeChatClient(streamChunks: new[] { "ok" }), Model(), new FakeModelCallLogger(), Ctx(),
            Opts(throwOnFailure: false, onCompleted: r => withStore = r), ImageStore());
        await foreach (var _ in client.GetStreamingResponseAsync(messages)) { }

        LlmCallResult? withoutStore = null;
        var bare = new ModelCallLoggingChatClient(
            new FakeChatClient(streamChunks: new[] { "ok" }), Model(), new FakeModelCallLogger(), Ctx(),
            Opts(throwOnFailure: false, onCompleted: r => withoutStore = r));
        await foreach (var _ in bare.GetStreamingResponseAsync(messages)) { }

        var image = Assert.Single(withStore!.RequestImages);
        Assert.Equal("a.png", image.Name);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Bytes);
        Assert.Empty(withoutStore!.RequestImages);
    }

    private static ModelCallImageStore ImageStore()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentWorkspace:RootPath"] = Path.GetTempPath() })
            .Build();
        return new ModelCallImageStore(
            new WorkspacePathResolver(config), config, NullLogger<ModelCallImageStore>.Instance);
    }

    // Model reasoning (gpt-5 family) tính cả token suy luận ẨN vào trần output. Lượt cần câu trả lời dài
    // thì nó tiêu sạch ngân sách trước khi kịp viết chữ nào: HTTP 200, finish_reason=length, content RỖNG.
    // Thông điệp "phản hồi có thể bị cắt" ở ca này là bẫy — người dùng bấm "Thử lại" mãi cũng cụt y hệt,
    // nên nó phải nói ra hai nút xoay được: nâng Context Window, hoặc đổi model.
    [Fact]
    public async Task TokenLimitWithNoOutput_SaysTheBudgetWasSpentAndHowToFixIt()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty))
        {
            FinishReason = ChatFinishReason.Length
        };
        var inner = new FakeChatClient(response: response);
        LlmCallResult? completed = null;
        var client = new ModelCallLoggingChatClient(
            inner, Model(), new FakeModelCallLogger(), Ctx(), Opts(throwOnFailure: false, onCompleted: r => completed = r));

        await client.GetResponseAsync(Hi());

        Assert.NotNull(completed);
        Assert.Contains("KHÔNG trả ra chữ nào", completed!.ErrorMessage);
        Assert.Contains("Context Window", completed.ErrorMessage);
    }

    // Ngược lại: có chữ mà bị cắt giữa chừng vẫn là ca cũ — câu trả lời cụt, không phải hết ngân sách vì
    // suy luận. Đừng đổ cho model reasoning khi nó thật sự đã viết được gì đó.
    [Fact]
    public async Task TokenLimitWithPartialOutput_KeepsTheTruncatedAnswerWording()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Câu trả lời dang d"))
        {
            FinishReason = ChatFinishReason.Length
        };
        var inner = new FakeChatClient(response: response);
        LlmCallResult? completed = null;
        var client = new ModelCallLoggingChatClient(
            inner, Model(), new FakeModelCallLogger(), Ctx(), Opts(throwOnFailure: false, onCompleted: r => completed = r));

        await client.GetResponseAsync(Hi());

        Assert.Contains("có thể bị cắt", completed!.ErrorMessage);
        Assert.DoesNotContain("KHÔNG trả ra chữ nào", completed.ErrorMessage);
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
