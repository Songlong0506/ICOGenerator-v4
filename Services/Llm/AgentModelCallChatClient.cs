using System.ClientModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ICOGenerator.Domain;
using ICOGenerator.Services.Logging;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Per-model-call middleware for the agent's native (function-calling) path. It sits directly above the
/// OpenAI <see cref="IChatClient"/> and below the function-invocation loop, so it sees exactly one model
/// round-trip per call. It owns the cross-cutting concerns that used to live inline in the hand-written
/// agent loop:
/// <list type="bullet">
///   <item>the single per-call deadline (the SDK's own network timeout is disabled in the factory);</item>
///   <item>the completion-token cap, recomputed per call from the current prompt size;</item>
///   <item>request-shape + DB logging via <see cref="IModelCallLogger"/> (call-log UI unchanged);</item>
///   <item>the per-step "thinking" progress line;</item>
///   <item>surfacing a failed call as a run-ending error (logged, then thrown).</item>
/// </list>
/// Text deltas are intentionally NOT surfaced here: the agent orchestrator streams them from the agent
/// response, so emitting them here too would double them.
/// </summary>
public sealed class AgentModelCallChatClient : DelegatingChatClient
{
    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };

    private readonly AiModel _model;
    private readonly Agent _agent;
    private readonly IModelCallLogger _modelCallLogger;
    private readonly Guid _projectId;
    private readonly Guid? _workflowRunId;
    private readonly int _requestTimeoutSeconds;
    private readonly int _maxSteps;
    private readonly int _hardCap;
    private readonly Action<string, string, string?>? _onProgress;

    private int _step;

    public AgentModelCallChatClient(
        IChatClient inner, AiModel model, Agent agent, IModelCallLogger modelCallLogger, Guid projectId,
        Guid? workflowRunId, int requestTimeoutSeconds, int maxSteps, int hardCap,
        Action<string, string, string?>? onProgress) : base(inner)
    {
        _model = model;
        _agent = agent;
        _modelCallLogger = modelCallLogger;
        _projectId = projectId;
        _workflowRunId = workflowRunId;
        _requestTimeoutSeconds = requestTimeoutSeconds;
        _maxSteps = maxSteps;
        _hardCap = hardCap;
        _onProgress = onProgress;
    }

    /// <summary>
    /// Number of model calls made so far this run. The orchestrator compares it against a phase's budget
    /// to tell a converged run (finished under budget) from one that exhausted its step budget.
    /// </summary>
    public int StepCount => _step;

    // The agent drives the streaming API; route any non-streaming call through it too so every model call
    // is logged identically and gets the same deadline + token cap.
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            updates.Add(update);
        return updates.ToChatResponse();
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var step = ++_step;
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();

        var stopwatch = Stopwatch.StartNew();
        var result = new LlmCallResult
        {
            Endpoint = _model.Endpoint,
            ModelId = _model.ModelId,
            ModelName = _model.Name,
            // ChatMessage.Text is empty for tool-result turns, so this slightly undercounts on tool-heavy
            // conversations — acceptable for an estimate used only for the cost/usage display.
            PromptTokens = TokenEstimator.Estimate(string.Join("\n", messageList.Select(m => m.Text)))
        };
        var maxTokens = MaxOutputTokenResolver.Resolve(_model, result.PromptTokens);

        // Recompute the per-call token cap; clone so we never mutate the options the agent shares.
        var callOptions = options?.Clone() ?? new ChatOptions();
        callOptions.MaxOutputTokens = maxTokens;

        // Logged in the shape the call-log UI expects; tools are summarised by name (the full JSON schema
        // is produced downstream by the OpenAI SDK from each AIFunction). The "thinking" field is injected
        // by ThinkingDisabledHandler in the HttpClient pipeline (see OpenAIChatClientFactory).
        result.RequestJson = JsonSerializer.Serialize(new
        {
            model = _model.ModelId,
            messages = messageList.Select(m => new { role = m.Role.Value, content = m.Text }),
            temperature = callOptions.Temperature,
            max_tokens = maxTokens,
            tools = callOptions.Tools?.Select(t => t.Name) ?? Enumerable.Empty<string>(),
            thinking = new { type = "disabled" }
        }, SerializeOptions);

        _onProgress?.Invoke("thinking", $"Agent {_agent.Name} đang suy nghĩ… (bước {BudgetLabel(step)})", null);

        // Link the caller's token (e.g. app shutdown) with the per-call deadline so either can unwind it.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_requestTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var token = linkedCts.Token;

        var updates = new List<ChatResponseUpdate>();
        await using var enumerator = base.GetStreamingResponseAsync(messageList, callOptions, token)
            .GetAsyncEnumerator(token);

        // Enumerate manually so a streaming error is caught and logged here (yield must stay out of try).
        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    break;
                update = enumerator.Current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller (e.g. app shutdown) cancelled — propagate so the worker treats it as a clean stop.
                throw;
            }
            catch (Exception ex)
            {
                await LogFailureAsync(result, stopwatch, step, ex).ConfigureAwait(false);
                throw new InvalidOperationException($"LLM call failed: {result.ErrorMessage}", ex);
            }

            updates.Add(update);
            yield return update;
        }

        stopwatch.Stop();
        var response = updates.ToChatResponse();
        var text = response.Text ?? string.Empty;
        result.Content = text;
        result.ExtractedContent = text;
        result.ResponseText = text;
        result.DurationMs = stopwatch.ElapsedMilliseconds;
        result.HttpStatusCode = 200;
        result.IsSuccess = true;
        // finish_reason == "length" means the model hit its token cap mid-output (often truncated JSON);
        // flag it so a cut-off answer is distinguishable from a clean one.
        if (response.FinishReason == ChatFinishReason.Length)
            result.ErrorMessage = "Phản hồi có thể bị cắt do đạt giới hạn token (finish_reason=length).";
        result.CompletionTokens = TokenEstimator.Estimate(text);
        result.TotalTokens = result.PromptTokens + result.CompletionTokens;

        await _modelCallLogger.LogAsync(_projectId, _agent, result, step, "AgentRun", _workflowRunId).ConfigureAwait(false);
    }

    private string BudgetLabel(int step) =>
        step <= _maxSteps ? $"{step}/{_maxSteps}" : $"{step}/{_hardCap} (chạy thêm để hoàn tất)";

    private async Task LogFailureAsync(LlmCallResult result, Stopwatch stopwatch, int step, Exception ex)
    {
        stopwatch.Stop();
        result.DurationMs = stopwatch.ElapsedMilliseconds;
        result.IsSuccess = false;
        switch (ex)
        {
            // Non-2xx from the API (incl. OpenAI-compatible servers). Keep the short message in the
            // DB-persisted, UI-visible fields.
            case ClientResultException api:
                result.HttpStatusCode = api.Status;
                result.ErrorMessage = $"API error: {api.Status}";
                result.Content = $"API error: {api.Status}\n\n{api.Message}";
                result.ResponseText = api.Message;
                break;
            // Our own deadline fired (stalled/slow stream).
            case OperationCanceledException:
                result.ErrorMessage = $"LLM request timed out after {_requestTimeoutSeconds}s.";
                result.Content = result.ErrorMessage;
                result.ResponseText = result.ErrorMessage;
                break;
            default:
                result.ErrorMessage = ex.Message;
                result.Content = ex.Message;
                result.ResponseText = ex.Message;
                break;
        }
        result.CompletionTokens = TokenEstimator.Estimate(result.Content);
        result.TotalTokens = result.PromptTokens + result.CompletionTokens;

        _onProgress?.Invoke("error", "Lời gọi LLM thất bại.", result.ErrorMessage);
        await _modelCallLogger.LogAsync(_projectId, _agent, result, step, "AgentRun", _workflowRunId).ConfigureAwait(false);
    }
}
