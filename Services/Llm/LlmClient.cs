using ICOGenerator.Domain;
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
    private readonly StructuredOutputPolicy _structuredOutputPolicy;
    private readonly IBudgetGuard _budgetGuard;
    private readonly ILogger<LlmClient> _logger;
    private readonly int _requestTimeoutSeconds;

    public LlmClient(IChatClientFactory chatClientFactory, IModelCallLogger modelCallLogger, StructuredOutputPolicy structuredOutputPolicy, IBudgetGuard budgetGuard, IConfiguration configuration, ILogger<LlmClient> logger)
    {
        _chatClientFactory = chatClientFactory;
        _modelCallLogger = modelCallLogger;
        _structuredOutputPolicy = structuredOutputPolicy;
        _budgetGuard = budgetGuard;
        _logger = logger;
        _requestTimeoutSeconds = configuration.GetValue("Llm:RequestTimeoutSeconds", DefaultRequestTimeoutSeconds);
    }

    public async Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
    {
        var result = await StreamOnceAsync(model, messages, temperature, logContext, onToken, cancellationToken).ConfigureAwait(false);

        // A model configured SupportsVision=true whose endpoint is actually text-only (e.g. DeepSeek) rejects
        // image parts with HTTP 400 "unknown variant `image_url`, expected `text`". Retry once without the
        // images so the turn survives on the text context; the real fix is unticking SupportsVision on the
        // Models page, which the warning points at.
        if (IsImageContentRejected(result) && ContainsImageContent(messages))
        {
            LogVisionMisconfigured(model);
            result = await StreamOnceAsync(model, StripImageContent(messages), temperature, logContext, onToken, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<LlmCallResult> StreamOnceAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken, CancellationToken cancellationToken)
    {
        // Compose the shared middleware over the per-model OpenAI client: it owns the deadline, token cap,
        // result-building, error mapping and DB logging that used to live inline here.
        LlmCallResult? captured = null;
        var client = BuildClient(model, logContext, r => captured = r);

        var options = new ChatOptions { Temperature = (float)temperature };

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
        // Models not opted into structured output keep the exact streaming + manual-parse behaviour
        // (including live token streaming for the requirement draft).
        if (!_structuredOutputPolicy.UseStructuredOutput(model))
        {
            var plain = await ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).ConfigureAwait(false);
            return (plain, null);
        }

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
