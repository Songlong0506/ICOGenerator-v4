using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Budget;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Đường gọi LLM "một lượt hỏi–đáp" (BA chat, chắt lọc bộ nhớ, các cổng soát) — khác đường agent ở chỗ
/// KHÔNG có vòng lặp tool. Bản thân class chỉ còn phần điều phối; mọi việc nặng đã nằm ở chỗ khác:
/// <see cref="ModelCallPipeline"/> (dựng client + middleware + giữ kết quả), <see cref="EndpointQuirks"/>
/// (endpoint từ chối cái gì thì bỏ cái đó rồi thử lại) và <see cref="LlmJson"/> (đọc JSON model trả về).
/// </summary>
public class LlmClient : ILlmClient
{
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IModelCallLogger _modelCallLogger;
    private readonly IBudgetGuard _budgetGuard;
    private readonly LlmSettings _settings;
    private readonly ILogger<LlmClient> _logger;
    private readonly ModelCallImageStore? _imageStore;

    // imageStore không bắt buộc để các test dựng tay LlmClient khỏi phải quan tâm tới việc lưu ảnh; app
    // thật luôn có (DI). Đây là đường DUY NHẤT hiện gửi ảnh cho model — tài liệu nguồn của BA chat và ảnh
    // chụp POC của bước chấm giao diện đều đi qua ChatStructuredAsync ở đây.
    public LlmClient(IChatClientFactory chatClientFactory, IModelCallLogger modelCallLogger, IBudgetGuard budgetGuard, LlmSettings settings, ILogger<LlmClient> logger, ModelCallImageStore? imageStore = null)
    {
        _chatClientFactory = chatClientFactory;
        _modelCallLogger = modelCallLogger;
        _budgetGuard = budgetGuard;
        _settings = settings;
        _logger = logger;
        _imageStore = imageStore;
    }

    public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) =>
        StreamWithRetryAsync(model, messages, temperature, logContext, onToken, responseFormat: null, cancellationToken);

    public async Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class
    {
        // response_format is opt-in per model via the Structured Output dropdown on the Models admin screen,
        // defaulting to None because many weak / local OpenAI-compatible servers reject the parameter. At every
        // level the caller keeps its own parser as a fallback, so a model returning JSON that misses the
        // expected shape still degrades gracefully instead of failing the turn.
        return model.StructuredOutputMode switch
        {
            StructuredOutputMode.JsonSchema => await ChatWithJsonSchemaAsync<T>(model, messages, temperature, logContext, onToken, cancellationToken).ConfigureAwait(false),
            StructuredOutputMode.JsonObject => await ChatWithJsonModeAsync<T>(model, messages, temperature, logContext, onToken, cancellationToken).ConfigureAwait(false),
            _ => (await ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).ConfigureAwait(false), null)
        };
    }

    // ── Đường stream (chat thuần + mức json_object) ───────────────────────────────────────────────────

    // The streaming call plus the text-only-endpoint retry, shared by the plain chat path and the JsonObject
    // structured path (which only differs by the response_format it asks for).
    private async Task<LlmCallResult> StreamWithRetryAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken, ChatResponseFormat? responseFormat, CancellationToken cancellationToken)
    {
        var result = await StreamOnceAsync(model, messages, temperature, logContext, onToken, responseFormat, cancellationToken).ConfigureAwait(false);

        // Hai lý do hỏng mà bỏ ảnh đi thì cứu được cả lượt (xem EndpointQuirks.ShouldRetryWithoutImages):
        // endpoint text-only từ chối content ảnh, và request chết ở tầng transport vì body quá lớn.
        if (EndpointQuirks.ShouldRetryWithoutImages(result, messages))
        {
            LogImageRetry(model, result);
            result = await StreamOnceAsync(model, EndpointQuirks.WithoutImageContent(messages), temperature, logContext, onToken, responseFormat, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private Task<LlmCallResult> StreamOnceAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken, ChatResponseFormat? responseFormat, CancellationToken cancellationToken) =>
        NewPipeline(model, logContext).StreamAsync(
            messages,
            new ChatOptions { Temperature = (float)temperature, ResponseFormat = responseFormat },
            onToken,
            cancellationToken);

    /// <summary>
    /// JSON mode: asks for <c>response_format: {"type":"json_object"}</c>, which guarantees syntactically valid
    /// JSON but says nothing about the shape — so the text is deserialized into <typeparamref name="T"/>
    /// leniently and a mismatch just yields a null value for the caller's own parser. Unlike the json_schema
    /// level this runs on the ordinary streaming path, so <paramref name="onToken"/> still drives the UI.
    /// </summary>
    private async Task<(LlmCallResult Result, T? Value)> ChatWithJsonModeAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken, CancellationToken cancellationToken) where T : class
    {
        // DeepSeek (and OpenAI's own json_object mode) reject the request unless the word "json" appears in the
        // prompt. Every prompt behind a structured call says it today, but prompts are edited by hand: skip the
        // parameter rather than spend a round trip on a guaranteed 400.
        if (!EndpointQuirks.MentionsJson(messages))
        {
            _logger.LogWarning(
                "Model {ModelId} đặt ở JSON mode nhưng prompt của lời gọi {Purpose} không chứa chữ \"json\" — "
                + "bỏ qua response_format cho lượt này. Hãy nhắc \"trả về JSON\" trong prompt để bật lại.",
                model.ModelId, logContext.Purpose);
            var plain = await ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).ConfigureAwait(false);
            return (plain, Deserialize<T>(plain));
        }

        var result = await StreamWithRetryAsync(model, messages, temperature, logContext, onToken, ChatResponseFormat.Json, cancellationToken).ConfigureAwait(false);

        // Endpoint turned out not to accept response_format at all: retry once on the plain text path so a
        // wrong Models-page setting costs a round trip instead of the whole feature.
        if (EndpointQuirks.RejectedResponseFormat(result))
        {
            LogStructuredOutputUnsupported(model, StructuredOutputMode.JsonObject, result);
            result = await StreamWithRetryAsync(model, messages, temperature, logContext, onToken, responseFormat: null, cancellationToken).ConfigureAwait(false);
        }

        return (result, Deserialize<T>(result));
    }

    // ── Đường json_schema (một round-trip, không stream) ──────────────────────────────────────────────

    private async Task<(LlmCallResult Result, T? Value)> ChatWithJsonSchemaAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken, CancellationToken cancellationToken) where T : class
    {
        var pipeline = NewPipeline(model, logContext);
        var options = new ChatOptions { Temperature = (float)temperature };

        try
        {
            // GetResponseAsync<T> sets response_format to a JSON schema derived from T and deserializes the
            // reply. It routes through our middleware's (non-streaming) GetResponseAsync, so the call is still
            // deadline-bounded, token-capped and logged identically.
            var response = await pipeline.Client.GetResponseAsync<T>(messages, options, useJsonSchemaResponseFormat: true, cancellationToken: cancellationToken).ConfigureAwait(false);

            // Same image fallback as ChatWithLogAsync (the middleware swallows the failure, so it surfaces
            // here as a captured result rather than an exception).
            if (EndpointQuirks.ShouldRetryWithoutImages(pipeline.Result, messages))
            {
                LogImageRetry(model, pipeline.Result);
                pipeline.Reset();
                response = await pipeline.Client.GetResponseAsync<T>(EndpointQuirks.WithoutImageContent(messages), options, useJsonSchemaResponseFormat: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var result = pipeline.Result;

            // Endpoint accepts chat but not json_schema — DeepSeek answers 400 "This response_format type is
            // unavailable now". The middleware swallows it into a failed result rather than throwing, so
            // without this branch the whole feature would go dark on one wrong Models-page setting. Drop to
            // the plain streaming path (which also restores onToken) and let the caller's parser take over.
            if (EndpointQuirks.RejectedResponseFormat(result))
            {
                LogStructuredOutputUnsupported(model, StructuredOutputMode.JsonSchema, result);
                var degraded = await ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).ConfigureAwait(false);
                return (degraded, Deserialize<T>(degraded));
            }

            // A 200 whose JSON doesn't fit T → null value → caller parses result.Content with its own parser.
            return result.IsSuccess && response.TryGetResult(out var value) ? (result, value) : (result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Structured output failed (schema gen / deserialization / unsupported server). Keep the flow
            // alive: reuse the captured result if the call reached the model, else do a plain call so the
            // caller can still parse text.
            _logger.LogWarning(ex, "Structured output for {ModelId} failed; falling back to manual parse.", model.ModelId);
            // Don't hand back a captured image failure: the plain path below retries it text-only.
            if (pipeline.HasResult && !EndpointQuirks.ShouldRetryWithoutImages(pipeline.Result, messages))
                return (pipeline.Result, null);

            var plain = await ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).ConfigureAwait(false);
            return (plain, null);
        }
    }

    // ── Hạ tầng dùng chung ───────────────────────────────────────────────────────────────────────────

    // Mỗi lời gọi một đường ống mới: ModelCallLoggingChatClient đếm bước theo instance, nên client riêng
    // giữ đúng số bước mà ModelCallLogContext.FirstStep khai báo cho call site này.
    private ModelCallPipeline NewPipeline(AiModel model, ModelCallLogContext logContext) =>
        new(_chatClientFactory, model, _modelCallLogger, logContext,
            new ModelCallOptions(_settings.RequestTimeoutSeconds, ThrowOnFailure: false) { BudgetGuard = _budgetGuard },
            _imageStore);

    // Shared last step of the JSON levels: pull the object out of the reply text (tolerating a ```json fence
    // or chatter around it, which json_object already prevents but None/degraded replies don't) and read it
    // into T. Anything unparseable is a null value, i.e. "caller, use your own parser" — including a reply
    // whose field names are entirely foreign to T (requireKnownProperty), which would otherwise deserialize
    // into an all-defaults object and look like a parse success.
    private static T? Deserialize<T>(LlmCallResult result) where T : class =>
        result.IsSuccess ? LlmJson.TryDeserialize<T>(result.Content, requireKnownProperty: true) : null;

    private void LogStructuredOutputUnsupported(AiModel model, StructuredOutputMode mode, LlmCallResult result) =>
        _logger.LogWarning(
            "Model {ModelId} từ chối response_format mức {Mode} — gọi lại bằng đường text thuần và parse tay. "
            + "Hãy hạ mức Structured Output của model này ở trang Models để hết vòng gọi thừa. Lỗi: {Error}",
            model.ModelId, mode, result.ResponseText);

    // Hai nguyên nhân, hai cách chữa khác hẳn nhau — nên log phải nói rõ nguyên nhân nào, thay vì một câu
    // "thử lại không kèm ảnh" chung chung khiến người vận hành đi tắt nhầm SupportsVision.
    private void LogImageRetry(AiModel model, LlmCallResult result)
    {
        if (EndpointQuirks.TransportFailed(result))
            _logger.LogWarning(
                "Lời gọi tới {ModelId} chết ở tầng transport khi đang gửi {ImageCount} ảnh (~{ImageBytes} bytes trước base64) "
                + "— thử lại KHÔNG kèm ảnh. Nếu tái diễn, hạ Llm:SourceUpload:MaxImagesPerCall / MaxTotalImageBytes: "
                + "nhiều endpoint và proxy đóng kết nối khi body vượt trần của chúng. Lỗi: {Error}",
                model.ModelId, result.RequestImageCount, result.RequestImageBytes, result.ResponseText);
        else
            _logger.LogWarning(
                "Model {ModelId} từ chối content ảnh (image_url) dù SupportsVision đang bật — thử lại không kèm ảnh. Tắt SupportsVision cho model này ở trang Models để hết cảnh báo.",
                model.ModelId);
    }
}

public class LlmCallResult
{
    public string Content { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string ResponseText { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public long DurationMs { get; set; }
    public int? HttpStatusCode { get; set; }
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Lỗi thuộc loại nào (xem <see cref="LlmFailureDescriber"/>). Vô nghĩa khi <see cref="IsSuccess"/>.
    /// Đây là thứ phân biệt "API từ chối" với "request chưa từng tới nơi" — hai thứ mà nguyên văn exception
    /// của SDK gói chung thành một câu "Retry failed after 4 tries", và cách chữa thì khác hẳn nhau.
    /// </summary>
    public LlmFailureKind FailureKind { get; set; }

    /// <summary>
    /// Số ẢNH và tổng dung lượng ảnh THỰC SỰ đi trong request này (tính từ chính danh sách message đã gửi,
    /// nên sau vòng thử lại không kèm ảnh thì cả hai về 0). Dùng để: (a) nói đúng nguyên nhân khi lời gọi
    /// chết ở tầng transport — body vài chục MB base64 hay bị chính endpoint/proxy cắt kết nối; (b) chặn
    /// việc khóa <see cref="ICOGenerator.Domain.ProjectSourceFile.VisionSummary"/> bằng một lượt mà model
    /// chưa hề nhìn thấy tấm ảnh nào.
    /// </summary>
    public int RequestImageCount { get; set; }

    public long RequestImageBytes { get; set; }

    /// <summary>
    /// Các ẢNH thực sự đi kèm lượt gọi này, theo đúng thứ tự chúng nằm trong <see cref="RequestJson"/>.
    /// Rỗng ở tuyệt đại đa số lời gọi (chỉ chat BA có tài liệu nguồn và lượt chấm giao diện POC mới có ảnh).
    /// Bytes đi cùng kết quả tới <see cref="IModelCallLogger"/> để lưu ra đĩa rồi thôi — KHÔNG vào DB.
    /// </summary>
    public IReadOnlyList<ModelCallImage> RequestImages { get; set; } = Array.Empty<ModelCallImage>();
}
