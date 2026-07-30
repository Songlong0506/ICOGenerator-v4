using System.Text.Json;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Budget;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

public class LlmClient : ILlmClient
{
    // Overall per-call deadline, enforced inside ModelCallLoggingChatClient (the SDK's own network timeout
    // is disabled in OpenAIChatClientFactory). Configurable via Llm:RequestTimeoutSeconds.
    private const int DefaultRequestTimeoutSeconds = 600;

    private readonly IChatClientFactory _chatClientFactory;
    private readonly IModelCallLogger _modelCallLogger;
    private readonly IBudgetGuard _budgetGuard;
    private readonly ILogger<LlmClient> _logger;
    private readonly int _requestTimeoutSeconds;

    public LlmClient(IChatClientFactory chatClientFactory, IModelCallLogger modelCallLogger, IBudgetGuard budgetGuard, IConfiguration configuration, ILogger<LlmClient> logger)
    {
        _chatClientFactory = chatClientFactory;
        _modelCallLogger = modelCallLogger;
        _budgetGuard = budgetGuard;
        _logger = logger;
        _requestTimeoutSeconds = configuration.GetValue("Llm:RequestTimeoutSeconds", DefaultRequestTimeoutSeconds);
    }

    public Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) =>
        StreamWithRetryAsync(model, messages, temperature, logContext, onToken, responseFormat: null, cancellationToken);

    // The streaming call plus the text-only-endpoint retry, shared by the plain chat path and the JsonObject
    // structured path (which only differs by the response_format it asks for).
    private async Task<LlmCallResult> StreamWithRetryAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken, ChatResponseFormat? responseFormat, CancellationToken cancellationToken)
    {
        var result = await StreamOnceAsync(model, messages, temperature, logContext, onToken, responseFormat, cancellationToken).ConfigureAwait(false);

        // A model configured SupportsVision=true whose endpoint is actually text-only (e.g. DeepSeek) rejects
        // image parts with HTTP 400 "unknown variant `image_url`, expected `text`". Retry once without the
        // images so the turn survives on the text context; the real fix is unticking SupportsVision on the
        // Models page, which the warning points at.
        if (IsImageContentRejected(result) && ContainsImageContent(messages))
        {
            LogVisionMisconfigured(model);
            result = await StreamOnceAsync(model, StripImageContent(messages), temperature, logContext, onToken, responseFormat, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<LlmCallResult> StreamOnceAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken, ChatResponseFormat? responseFormat, CancellationToken cancellationToken)
    {
        // Compose the shared middleware over the per-model OpenAI client: it owns the deadline, token cap,
        // result-building, error mapping and DB logging that used to live inline here.
        LlmCallResult? captured = null;
        var client = BuildClient(model, logContext, r => captured = r);

        var options = new ChatOptions { Temperature = (float)temperature, ResponseFormat = responseFormat };

        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            var text = update.Text;
            // Surface the delta live. A misbehaving sink must never break the LLM call, so swallow anything
            // it throws (the buffered result is still returned).
            if (!string.IsNullOrEmpty(text) && onToken != null)
            {
                try { onToken(text); }
                catch { /* ignore UI streaming failures */ }
            }
        }

        // The middleware reports the built result (success or swallowed failure) via the onCompleted callback.
        return captured ?? throw new InvalidOperationException("Model call produced no result.");
    }

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
        if (!MentionsJson(messages))
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
        if (IsResponseFormatRejected(result))
        {
            LogStructuredOutputUnsupported(model, StructuredOutputMode.JsonObject, result);
            result = await StreamWithRetryAsync(model, messages, temperature, logContext, onToken, responseFormat: null, cancellationToken).ConfigureAwait(false);
        }

        return (result, Deserialize<T>(result));
    }

    private async Task<(LlmCallResult Result, T? Value)> ChatWithJsonSchemaAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken, CancellationToken cancellationToken) where T : class
    {
        LlmCallResult? captured = null;
        var client = BuildClient(model, logContext, r => captured = r);

        var options = new ChatOptions { Temperature = (float)temperature };

        try
        {
            // GetResponseAsync<T> sets response_format to a JSON schema derived from T and deserializes the
            // reply. It routes through our middleware's (non-streaming) GetResponseAsync, so the call is still
            // deadline-bounded, token-capped and logged identically.
            var response = await client.GetResponseAsync<T>(messages, options, useJsonSchemaResponseFormat: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            var result = captured ?? throw new InvalidOperationException("Model call produced no result.");

            // Same text-only-endpoint fallback as ChatWithLogAsync (the middleware swallows the 400, so it
            // surfaces here as a captured failure rather than an exception).
            if (IsImageContentRejected(result) && ContainsImageContent(messages))
            {
                LogVisionMisconfigured(model);
                captured = null;
                response = await client.GetResponseAsync<T>(StripImageContent(messages), options, useJsonSchemaResponseFormat: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                result = captured ?? throw new InvalidOperationException("Model call produced no result.");
            }

            // Endpoint accepts chat but not json_schema — DeepSeek answers 400 "This response_format type is
            // unavailable now". The middleware swallows it into a failed result rather than throwing, so
            // without this branch the whole feature would go dark on one wrong Models-page setting. Drop to
            // the plain streaming path (which also restores onToken) and let the caller's parser take over.
            if (IsResponseFormatRejected(result))
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
            // Don't hand back a captured image-rejection failure: the plain path below retries it text-only.
            if (captured != null && !(IsImageContentRejected(captured) && ContainsImageContent(messages)))
                return (captured, null);

            var plain = await ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).ConfigureAwait(false);
            return (plain, null);
        }
    }

    // OpenAI-compatible endpoints that don't implement the requested response_format level answer 400 naming
    // the parameter — DeepSeek: "This response_format type is unavailable now"; local servers usually
    // "response_format is not supported". Matching the parameter name covers both without a per-vendor table.
    private static bool IsResponseFormatRejected(LlmCallResult result) =>
        !result.IsSuccess && result.ResponseText.Contains("response_format", StringComparison.OrdinalIgnoreCase);

    // JSON mode is refused outright unless the prompt itself mentions JSON (documented for both DeepSeek and
    // OpenAI), so check before spending the call.
    private static bool MentionsJson(List<ChatMessage> messages) =>
        messages.Any(m => m.Text.Contains("json", StringComparison.OrdinalIgnoreCase));

    // Shared last step of the JSON levels: pull the object out of the reply text (tolerating a ```json fence
    // or chatter around it, which json_object already prevents but None/degraded replies don't) and read it
    // into T. Anything unparseable is a null value, i.e. "caller, use your own parser".
    private static T? Deserialize<T>(LlmCallResult result) where T : class
    {
        if (!result.IsSuccess)
            return null;

        var json = JsonExtractor.Extract(result.Content);
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            // json_object guarantees valid JSON but NOT the shape, and System.Text.Json happily turns a reply
            // with entirely foreign field names into an all-defaults T. Handing that back would look like a
            // parse success and quietly rob the caller of its fallback parser, so demand that the reply and T
            // agree on at least one field before trusting it.
            if (!SharesAnyFieldWith<T>(json))
                return null;

            return JsonSerializer.Deserialize<T>(json, JsonDefaults.CaseInsensitive);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool SharesAnyFieldWith<T>(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return false;

        var expected = typeof(T)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return document.RootElement.EnumerateObject().Any(p => expected.Contains(p.Name));
    }

    private void LogStructuredOutputUnsupported(AiModel model, StructuredOutputMode mode, LlmCallResult result) =>
        _logger.LogWarning(
            "Model {ModelId} từ chối response_format mức {Mode} — gọi lại bằng đường text thuần và parse tay. "
            + "Hãy hạ mức Structured Output của model này ở trang Models để hết vòng gọi thừa. Lỗi: {Error}",
            model.ModelId, mode, result.ResponseText);

    // Text-only OpenAI-compatible endpoints (DeepSeek among them) reject image parts with a 400 whose body
    // names the offending content type: "unknown variant `image_url`, expected `text`".
    private static bool IsImageContentRejected(LlmCallResult result) =>
        !result.IsSuccess && result.ResponseText.Contains("image_url", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsImageContent(List<ChatMessage> messages) =>
        messages.Any(m => m.Contents.Any(c => c is DataContent or UriContent));

    private static List<ChatMessage> StripImageContent(List<ChatMessage> messages) =>
        messages.Select(m =>
        {
            if (!m.Contents.Any(c => c is DataContent or UriContent))
                return m;
            var kept = m.Contents.Where(c => c is not DataContent and not UriContent).ToList();
            if (kept.Count == 0)
                kept.Add(new TextContent("(ảnh đính kèm bị bỏ qua vì model không nhận ảnh)"));
            return new ChatMessage(m.Role, kept);
        }).ToList();

    private void LogVisionMisconfigured(AiModel model) =>
        _logger.LogWarning(
            "Model {ModelId} từ chối content ảnh (image_url) dù SupportsVision đang bật — thử lại không kèm ảnh. Tắt SupportsVision cho model này ở trang Models để hết cảnh báo.",
            model.ModelId);

    // The chat client wraps the pooled (IHttpClientFactory-owned) HttpClient, so the OpenAI client is
    // intentionally lightweight/per-call; the shared middleware is layered on via ChatClientBuilder.
    private IChatClient BuildClient(AiModel model, ModelCallLogContext logContext, Action<LlmCallResult> onCompleted) =>
        _chatClientFactory.Create(model)
            .AsBuilder()
            .Use(inner => new ModelCallLoggingChatClient(
                inner, model, _modelCallLogger, logContext, _requestTimeoutSeconds,
                throwOnFailure: false, onCompleted: onCompleted, budgetGuard: _budgetGuard))
            .Build();
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
}
