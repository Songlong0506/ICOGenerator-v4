using System.Runtime.CompilerServices;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
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
    private static AiModel Model(StructuredOutputMode structuredOutputMode = StructuredOutputMode.None) =>
        new() { ModelId = "deepseek-v4-flash", Endpoint = "https://api.deepseek.com", StructuredOutputMode = structuredOutputMode };
    private static ModelCallLogContext Ctx() => new(Guid.NewGuid(), new Agent(), "TestPurpose");

    private const string ImageRejectedError =
        "HTTP 400 (invalid_request_error: invalid_request_error)\r\n\r\nFailed to deserialize the JSON body into the target type: messages[22]: unknown variant `image_url`, expected `text` at line 1 column 185749";

    // DeepSeek's answer to response_format: json_schema — the 400 that made the per-model bool a bad fit.
    private const string ResponseFormatRejectedError =
        "HTTP 400 (invalid_request_error: invalid_request_error)\r\n\r\nThis response_format type is unavailable now";

    /// <summary>
    /// Lời gọi KHÔNG tới được endpoint: không có phản hồi nào để mà đọc mã lỗi, chỉ có bó lần-thử của
    /// chính sách retry trong System.ClientModel. Đây đúng là chuỗi người dùng nhìn thấy trong bong bóng ⚠️
    /// khi đính kèm .docx nhiều hình — xem <see cref="FakeChatClientFactory"/> để biết nó được dựng thành
    /// exception hình dạng thật (AggregateException bọc HttpRequestException) chứ không phải chuỗi suông.
    /// </summary>
    private const string TransportFailure =
        "Retry failed after 4 tries. (An error occurred while sending the request.) (An error occurred while sending the request.) "
        + "(An error occurred while sending the request.) (An error occurred while sending the request.)";

    private static List<ChatMessage> MessagesWithImage() => new()
    {
        // "json" in the prompt is what JSON mode requires, and every real prompt behind a structured call
        // says it — keep the fixture honest about that.
        new ChatMessage(ChatRole.System, "system prompt: trả lời bằng json"),
        new ChatMessage(ChatRole.User, new List<AIContent>
        {
            new TextContent("user turn with attachment"),
            new DataContent(new byte[] { 1, 2, 3 }, "image/png")
        })
    };

    private static List<ChatMessage> TextMessages(string text = "trả lời bằng json") => new()
    {
        new ChatMessage(ChatRole.User, text)
    };

    private static LlmClient Client(FakeChatClientFactory factory) => new(
        factory,
        new FakeModelCallLogger(),
        new NoopBudgetGuard(),
        new LlmSettings(),
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

    // Đính kèm .docx nhiều hình chụp màn hình ⇒ request phình lên hàng chục MB base64 và gateway trước
    // endpoint đóng thẳng kết nối: lời gọi chết KHÔNG có mã HTTP, thông điệp là bó lần-thử của SDK
    // ("Retry failed after 4 tries. (An error occurred while sending the request.) …"). Thử lại y nguyên
    // thì lần nào cũng chết như nhau — chỉ bỏ ảnh mới cứu được lượt, và người dùng vẫn nhận được câu trả
    // lời dựa trên phần chữ thay vì một bong bóng ⚠️.
    [Fact]
    public async Task ChatWithLog_RetriesWithoutImages_WhenRequestNeverReachesTheEndpoint()
    {
        var factory = new FakeChatClientFactory(rejectImagesWith: TransportFailure, replyText: "ok");
        var client = Client(factory);

        var result = await client.ChatWithLogAsync(Model(), MessagesWithImage(), 0.3, Ctx());

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", result.Content);
        Assert.Equal(2, factory.Calls.Count);
        Assert.DoesNotContain(factory.Calls[1].SelectMany(m => m.Contents), c => c is DataContent);
        // Caller PHẢI biết ảnh không hề đi ra: nó là điều kiện để KHÔNG khóa "nội dung hình" thành chữ.
        Assert.True(result.ImagesDropped);
        // Và model phải được đính chính, nếu không nó mô tả 12 tấm hình chưa từng nhìn thấy.
        Assert.Contains(factory.Calls[1].SelectMany(m => m.Contents).OfType<TextContent>(),
            t => t.Text.Contains("KHÔNG gửi kèm"));
    }

    [Fact]
    public async Task ChatStructured_RetriesWithoutImages_WhenRequestNeverReachesTheEndpoint()
    {
        var factory = new FakeChatClientFactory(rejectImagesWith: TransportFailure, replyText: """{"answer":"ok"}""");
        var client = Client(factory);

        var (result, value) = await client.ChatStructuredAsync<StructuredReply>(
            Model(StructuredOutputMode.JsonSchema), MessagesWithImage(), 0.3, Ctx());

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", value?.Answer);
        Assert.True(result.ImagesDropped);
        Assert.DoesNotContain(factory.Calls[1].SelectMany(m => m.Contents), c => c is DataContent);
    }

    // Lượt text thuần chết vì mạng thì bỏ ảnh chẳng cứu được gì (không có ảnh nào để bỏ) — đừng đốt thêm
    // một round-trip, và đừng báo ImagesDropped khiến caller tưởng có ảnh bị bỏ.
    [Fact]
    public async Task ChatWithLog_DoesNotRetry_WhenTransportFailsOnATextOnlyTurn()
    {
        var factory = new FakeChatClientFactory(alwaysFailWith: TransportFailure);
        var client = Client(factory);

        var result = await client.ChatWithLogAsync(Model(), TextMessages(), 0.3, Ctx());

        Assert.False(result.IsSuccess);
        Assert.Single(factory.Calls);
        Assert.False(result.ImagesDropped);
    }

    // Endpoint không kham nổi mức structured output đang xin có HAI cách biểu hiện. Cách "lịch sự" là 400
    // nêu tên tham số — nhánh đã có. Cách còn lại là nhận request rồi ĐÓNG kết nối không trả xong phản hồi
    // ("The response ended prematurely"), thường gặp khi decoding bị ép theo schema kéo dài quá timeout của
    // gateway. Không có mã HTTP để bắt, nên nếu chỉ nhìn 400 thì cả tính năng tắt ngóm.
    [Theory]
    [InlineData(StructuredOutputMode.JsonSchema)]
    [InlineData(StructuredOutputMode.JsonObject)]
    public async Task ChatStructured_FallsBackToPlainText_WhenTheStructuredCallDiesMidResponse(StructuredOutputMode mode)
    {
        var factory = new FakeChatClientFactory(
            rejectResponseFormatWith: TransportFailure, replyText: """{"answer":"ok"}""");
        var client = Client(factory);

        var (result, value) = await client.ChatStructuredAsync<StructuredReply>(Model(mode), TextMessages(), 0.3, Ctx());

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", value?.Answer);
        Assert.Equal(2, factory.Options.Count);
        Assert.Null(factory.Options[1].ResponseFormat);
        // Lượt cứu phải chạy trên đường STREAM: mức json_schema vốn là một round-trip câm, mà kết nối im
        // lặng suốt lượt sinh chính là thứ gateway cắt.
        Assert.Same(factory.Calls[1], factory.Streamed[^1]);
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

        var (result, value) = await client.ChatStructuredAsync<StructuredReply>(Model(StructuredOutputMode.JsonSchema), MessagesWithImage(), 0.3, Ctx());

        Assert.True(result.IsSuccess);
        Assert.NotNull(value);
        Assert.Equal("ok", value!.Answer);
        Assert.Equal(2, factory.Calls.Count);
        Assert.DoesNotContain(factory.Calls[1].SelectMany(m => m.Contents), c => c is DataContent);
    }

    // JsonObject is the level DeepSeek actually implements. Two things must hold that json_schema can't give:
    // response_format goes out as a plain JSON-object request, and the call stays on the STREAMING path so the
    // "model is typing" callback still fires.
    [Fact]
    public async Task ChatStructured_JsonObject_AsksForJsonModeAndKeepsStreaming()
    {
        var factory = new FakeChatClientFactory(replyText: """{"answer":"ok"}""");
        var client = Client(factory);
        var streamed = new List<string>();

        var (result, value) = await client.ChatStructuredAsync<StructuredReply>(
            Model(StructuredOutputMode.JsonObject), TextMessages(), 0.3, Ctx(), streamed.Add);

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", value?.Answer);
        Assert.Equal("""{"answer":"ok"}""", Assert.Single(streamed));
        Assert.Single(factory.Streamed);
        Assert.Empty(factory.NonStreamed);
        // A bare json_object request, i.e. no schema attached.
        var format = Assert.IsType<ChatResponseFormatJson>(Assert.Single(factory.Options).ResponseFormat);
        Assert.Null(format.Schema);
    }

    // json_object only guarantees valid JSON, never the shape. A reply whose fields are entirely foreign to T
    // must come back as a null value — System.Text.Json would otherwise deserialize it into an all-defaults
    // object, which reads as success and silently robs the caller of its own parser.
    [Fact]
    public async Task ChatStructured_JsonObject_ReturnsNullValue_WhenJsonDoesNotFitTheType()
    {
        var factory = new FakeChatClientFactory(replyText: """{"unexpected":[1,2,3]}""");
        var client = Client(factory);

        var (result, value) = await client.ChatStructuredAsync<StructuredReply>(
            Model(StructuredOutputMode.JsonObject), TextMessages(), 0.3, Ctx());

        Assert.True(result.IsSuccess);
        Assert.Null(value);
    }

    // Both JSON levels are refused unless the prompt mentions json, so a prompt edited to drop the word must
    // skip response_format instead of spending a round trip on a guaranteed 400.
    [Fact]
    public async Task ChatStructured_JsonObject_SkipsResponseFormat_WhenPromptNeverMentionsJson()
    {
        var factory = new FakeChatClientFactory(replyText: """{"answer":"ok"}""");
        var client = Client(factory);

        var (result, value) = await client.ChatStructuredAsync<StructuredReply>(
            Model(StructuredOutputMode.JsonObject), TextMessages("liệt kê các nhóm còn thiếu"), 0.3, Ctx());

        Assert.True(result.IsSuccess);
        // Still parsed from the text — the fallback path, not a failure.
        Assert.Equal("ok", value?.Answer);
        Assert.Null(Assert.Single(factory.Options).ResponseFormat);
    }

    // The reported bug: ticking structured output for DeepSeek turned every JSON gate dark, because the
    // middleware swallows the 400 into a failed result and callers bail on !IsSuccess. One plain-text retry
    // turns a wrong Models-page setting back into a working (if slower) turn.
    [Theory]
    [InlineData(StructuredOutputMode.JsonSchema)]
    [InlineData(StructuredOutputMode.JsonObject)]
    public async Task ChatStructured_FallsBackToPlainText_WhenEndpointRejectsResponseFormat(StructuredOutputMode mode)
    {
        var factory = new FakeChatClientFactory(
            rejectResponseFormatWith: ResponseFormatRejectedError, replyText: """{"answer":"ok"}""");
        var client = Client(factory);

        var (result, value) = await client.ChatStructuredAsync<StructuredReply>(Model(mode), TextMessages(), 0.3, Ctx());

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", value?.Answer);
        // Second attempt carries no response_format at all.
        Assert.Equal(2, factory.Options.Count);
        Assert.Null(factory.Options[1].ResponseFormat);
    }

    [Fact]
    public async Task ChatStructured_None_SendsNoResponseFormatAndLeavesParsingToTheCaller()
    {
        var factory = new FakeChatClientFactory(replyText: """{"answer":"ok"}""");
        var client = Client(factory);

        var (result, value) = await client.ChatStructuredAsync<StructuredReply>(
            Model(), TextMessages(), 0.3, Ctx());

        Assert.True(result.IsSuccess);
        Assert.Null(value);
        Assert.Null(Assert.Single(factory.Options).ResponseFormat);
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
        private readonly string? _rejectResponseFormatWith;
        private readonly string? _alwaysFailWith;
        private readonly string _replyText;

        public List<List<ChatMessage>> Calls { get; } = new();
        /// <summary>ChatOptions of every call, in order — lets a test assert on the response_format actually asked for.</summary>
        public List<ChatOptions> Options { get; } = new();
        /// <summary>Calls that went through the streaming API vs the single round-trip one, to pin which path a level uses.</summary>
        public List<List<ChatMessage>> Streamed { get; } = new();
        public List<List<ChatMessage>> NonStreamed { get; } = new();

        public FakeChatClientFactory(string? rejectImagesWith = null, string? rejectResponseFormatWith = null, string? alwaysFailWith = null, string replyText = "ok")
        {
            _rejectImagesWith = rejectImagesWith;
            _rejectResponseFormatWith = rejectResponseFormatWith;
            _alwaysFailWith = alwaysFailWith;
            _replyText = replyText;
        }

        public IChatClient Create(AiModel model) => new FakeChatClient(this);

        private void Record(List<ChatMessage> messages, ChatOptions? options, bool streaming)
        {
            Calls.Add(messages);
            Options.Add(options ?? new ChatOptions());
            (streaming ? Streamed : NonStreamed).Add(messages);
        }

        private Exception? FailureFor(List<ChatMessage> messages, ChatOptions? options)
        {
            var failure = _alwaysFailWith
                ?? (options?.ResponseFormat is not null ? _rejectResponseFormatWith : null)
                ?? (messages.Any(m => m.Contents.Any(c => c is DataContent)) ? _rejectImagesWith : null);
            return failure == null ? null : AsException(failure);
        }

        // Lỗi TẦNG TRUYỀN TẢI phải mang đúng hình dạng thật, không thể giả bằng một exception bất kỳ có
        // cùng nội dung chữ: LlmClient phân biệt "endpoint trả lỗi" với "request không tới nơi" bằng HỌ
        // exception (HttpRequestException, có thể nằm trong AggregateException do chính sách retry của SDK
        // gói lại), chứ không bằng cách so chuỗi — nếu không thì mọi lỗi lạ đều bị coi là lỗi mạng.
        private static Exception AsException(string failure) =>
            failure.StartsWith("Retry failed after", StringComparison.Ordinal)
                ? new AggregateException(failure,
                    Enumerable.Range(0, 4).Select(_ => (Exception)new HttpRequestException("An error occurred while sending the request.")))
                : new InvalidOperationException(failure);

        private sealed class FakeChatClient : IChatClient
        {
            private readonly FakeChatClientFactory _owner;

            public FakeChatClient(FakeChatClientFactory owner) => _owner = owner;

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                var list = messages.ToList();
                _owner.Record(list, options, streaming: false);
                var failure = _owner.FailureFor(list, options);
                if (failure != null)
                    throw failure;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _owner._replyText)));
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                var list = messages.ToList();
                _owner.Record(list, options, streaming: true);
                await Task.CompletedTask;
                var failure = _owner.FailureFor(list, options);
                if (failure != null)
                    throw failure;
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
