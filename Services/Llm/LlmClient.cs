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
            if (captured != null)
                return (captured, null);

            var plain = await ChatWithLogAsync(model, messages, temperature, logContext, onToken, cancellationToken).ConfigureAwait(false);
            return (plain, null);
        }
    }

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
    public string Endpoint { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public long DurationMs { get; set; }
    public int? HttpStatusCode { get; set; }
    public bool IsSuccess { get; set; }
}
