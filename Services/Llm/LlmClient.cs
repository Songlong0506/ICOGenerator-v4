using ICOGenerator.Domain;
using ICOGenerator.Services.Logging;
using Microsoft.Agents.AI;
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
    private readonly ILogger<LlmClient> _logger;
    private readonly int _requestTimeoutSeconds;

    public LlmClient(IChatClientFactory chatClientFactory, IModelCallLogger modelCallLogger, StructuredOutputPolicy structuredOutputPolicy, IConfiguration configuration, ILogger<LlmClient> logger)
    {
        _chatClientFactory = chatClientFactory;
        _modelCallLogger = modelCallLogger;
        _structuredOutputPolicy = structuredOutputPolicy;
        _logger = logger;
        _requestTimeoutSeconds = configuration.GetValue("Llm:RequestTimeoutSeconds", DefaultRequestTimeoutSeconds);
    }

    public async Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default)
    {
        // The BA runs on the SAME Microsoft Agent Framework abstraction as the worker roles — a
        // ChatClientAgent over the per-model OpenAI client + shared logging middleware — it just advertises no
        // tools, so this is a plain streamed turn. The deadline, token cap, result-building, error mapping and
        // DB logging still come from the composed ModelCallLoggingChatClient (handed back via onCompleted).
        LlmCallResult? captured = null;
        var (agent, session) = await BuildAgentSessionAsync(model, logContext, r => captured = r, cancellationToken).ConfigureAwait(false);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Temperature = (float)temperature });

        await foreach (var update in agent.RunStreamingAsync(messages, session, runOptions, cancellationToken).ConfigureAwait(false))
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
        var (agent, session) = await BuildAgentSessionAsync(model, logContext, r => captured = r, cancellationToken).ConfigureAwait(false);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Temperature = (float)temperature });

        try
        {
            // RunAsync<T> derives a json-schema response_format from T and deserializes the reply into
            // AgentResponse<T>.Result. It routes through our middleware's (non-streaming) GetResponseAsync, so
            // the call is still deadline-bounded, token-capped and logged identically.
            var response = await agent.RunAsync<T>(messages, session, AIJsonUtilities.DefaultOptions, runOptions, cancellationToken).ConfigureAwait(false);
            var result = captured ?? throw new InvalidOperationException("Model call produced no result.");

            // A 200 whose JSON doesn't fit T → null value → caller parses result.Content with its own parser.
            return result.IsSuccess && response.Result is { } value ? (result, value) : (result, null);
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

    // Builds the BA's ChatClientAgent over the per-model OpenAI client wrapped with the shared
    // logging/deadline middleware, plus a fresh stateless session (history is owned by the caller's DB, not
    // the session). UseProvidedChatClientAsIs keeps the composed pipeline intact so the agent doesn't wrap it
    // again; the OpenAI client is intentionally lightweight/per-call (it wraps the pooled HttpClient).
    private async Task<(ChatClientAgent Agent, AgentSession Session)> BuildAgentSessionAsync(
        AiModel model, ModelCallLogContext logContext, Action<LlmCallResult> onCompleted, CancellationToken cancellationToken)
    {
        var client = _chatClientFactory.Create(model)
            .AsBuilder()
            .Use(inner => new ModelCallLoggingChatClient(
                inner, model, _modelCallLogger, logContext, _requestTimeoutSeconds,
                throwOnFailure: false, onCompleted: onCompleted))
            .Build();

        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = logContext.Agent.Name,
            UseProvidedChatClientAsIs = true
        });

        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        return (agent, session);
    }
}

public class LlmCallResult
{
    public string Content { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string ResponseText { get; set; } = string.Empty;
    public string? ExtractedContent { get; set; }
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
