using ICOGenerator.Domain;
using Microsoft.Extensions.AI;
using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ICOGenerator.Services.Llm;

public class LlmClient : ILlmClient
{
    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };

    // Overall per-call deadline, enforced via a linked CancellationToken below (the SDK's own network
    // timeout is disabled in OpenAIChatClientFactory). Configurable via Llm:RequestTimeoutSeconds.
    private const int DefaultRequestTimeoutSeconds = 600;

    // Upper bound for completion tokens, plus prompt headroom so small-context models aren't
    // asked for more output than fits.
    private const int MaxCompletionTokens = 100000;
    private const int ContextSafetyMargin = 1024;

    private readonly IChatClientFactory _chatClientFactory;
    private readonly ILogger<LlmClient> _logger;
    private readonly int _requestTimeoutSeconds;

    public LlmClient(IChatClientFactory chatClientFactory, IConfiguration configuration, ILogger<LlmClient> logger)
    {
        _chatClientFactory = chatClientFactory;
        _logger = logger;
        _requestTimeoutSeconds = configuration.GetValue("Llm:RequestTimeoutSeconds", DefaultRequestTimeoutSeconds);
    }

    public async Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessageDto> messages, double temperature, Action<string>? onToken = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new LlmCallResult
        {
            Endpoint = model.Endpoint,
            ModelId = model.ModelId,
            ModelName = model.Name,
            PromptTokens = TokenEstimator.Estimate(string.Join("\n", messages.Select(x => x.Content)))
        };

        var maxTokens = ResolveMaxTokens(model, result.PromptTokens);

        // Log the request in the same shape as before so the call-log UI is unchanged. The actual wire
        // request is now produced by the OpenAI SDK; the "thinking" field is injected by
        // ThinkingDisabledHandler in the HttpClient pipeline (see OpenAIChatClientFactory).
        result.RequestJson = JsonSerializer.Serialize(new ChatCompletionRequestDto
        {
            Model = model.ModelId,
            Messages = messages,
            Temperature = temperature,
            MaxTokens = maxTokens,
            Stream = true,
            Thinking = new ThinkingDto { Type = "disabled" }
        }, SerializeOptions);

        // Link the caller's token (e.g. app shutdown) with the request deadline so either can unwind the call.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_requestTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var token = linkedCts.Token;

        // The chat client wraps the pooled (IHttpClientFactory-owned) HttpClient, so it is intentionally
        // NOT disposed here — disposing it could tear down the shared connection pool.
        var chatClient = _chatClientFactory.Create(model);

        var chatMessages = messages.Select(m => new ChatMessage(MapRole(m.Role), m.Content)).ToList();
        var options = new ChatOptions
        {
            Temperature = (float)temperature,
            MaxOutputTokens = maxTokens
        };

        try
        {
            var contentBuilder = new StringBuilder();
            ChatFinishReason? finishReason = null;

            await foreach (var update in chatClient.GetStreamingResponseAsync(chatMessages, options, token))
            {
                if (update.FinishReason is { } reason)
                    finishReason = reason;

                var text = update.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    contentBuilder.Append(text);

                    // Surface the delta live. A misbehaving sink must never break the LLM call, so swallow
                    // anything it throws (the buffered result is still returned).
                    if (onToken != null)
                    {
                        try { onToken(text); }
                        catch { /* ignore UI streaming failures */ }
                    }
                }
            }

            stopwatch.Stop();

            result.Content = contentBuilder.ToString();
            result.ExtractedContent = result.Content;
            result.ResponseText = result.Content;
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            result.HttpStatusCode = 200;
            result.IsSuccess = true;
            // finish_reason == "length" means the model hit its token cap mid-output (often truncated
            // JSON); flag it so a cut-off answer is distinguishable from a clean one.
            if (finishReason == ChatFinishReason.Length)
                result.ErrorMessage = "Phản hồi có thể bị cắt do đạt giới hạn token (finish_reason=length).";
            result.CompletionTokens = TokenEstimator.Estimate(result.Content);
            result.TotalTokens = result.PromptTokens + result.CompletionTokens;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller (e.g. app shutdown) cancelled — propagate so the worker treats it as a clean stop.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own deadline fired (stalled/slow stream): return a failed result instead of hanging.
            stopwatch.Stop();
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            result.IsSuccess = false;
            result.ErrorMessage = $"LLM request timed out after {_requestTimeoutSeconds}s.";
            result.Content = result.ErrorMessage;
            result.ResponseText = result.ErrorMessage;
            result.CompletionTokens = TokenEstimator.Estimate(result.Content);
            result.TotalTokens = result.PromptTokens + result.CompletionTokens;
            return result;
        }
        catch (ClientResultException ex)
        {
            // Non-2xx from the API (incl. OpenAI-compatible servers). Keep the short message in the
            // DB-persisted, UI-visible fields; the full exception goes to the logger.
            stopwatch.Stop();
            _logger.LogError(ex, "LLM call to {Endpoint} ({ModelId}) failed.", model.Endpoint, model.ModelId);
            result.HttpStatusCode = ex.Status;
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            result.IsSuccess = false;
            result.ErrorMessage = $"API error: {ex.Status}";
            result.Content = $"API error: {ex.Status}\n\n{ex.Message}";
            result.ResponseText = ex.Message;
            result.CompletionTokens = TokenEstimator.Estimate(result.Content);
            result.TotalTokens = result.PromptTokens + result.CompletionTokens;
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Log the full exception, but keep only the short message in DB-persisted, UI-visible
            // fields so internal stack/paths aren't leaked.
            _logger.LogError(ex, "LLM call to {Endpoint} ({ModelId}) failed.", model.Endpoint, model.ModelId);
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            result.IsSuccess = false;
            result.ErrorMessage = ex.Message;
            result.Content = ex.Message;
            result.ResponseText = ex.Message;
            result.CompletionTokens = TokenEstimator.Estimate(result.Content);
            result.TotalTokens = result.PromptTokens + result.CompletionTokens;
            return result;
        }
    }

    private static ChatRole MapRole(string role) => role?.ToLowerInvariant() switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User
    };

    // Cap completion tokens to what's left in the context window after the (estimated) prompt;
    // a fixed 100k overflows small-context models. Falls back to the cap when the window is unknown.
    private static int ResolveMaxTokens(AiModel model, int promptTokens)
    {
        if (model.ContextWindow <= 0)
            return MaxCompletionTokens;

        var available = model.ContextWindow - promptTokens - ContextSafetyMargin;
        return Math.Clamp(available, 256, MaxCompletionTokens);
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
