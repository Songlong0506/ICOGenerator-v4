using System.ClientModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ICOGenerator.Domain;
using ICOGenerator.Services.Logging;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// The single piece of per-model-call middleware shared by BOTH execution paths — the agent's native
/// function-calling loop (<see cref="ICOGenerator.Services.Agents.AgentRunService"/>) and the plain
/// streaming/structured chat used by <see cref="LlmClient"/> (BA, readiness, the prompt-based fallback).
/// As a <see cref="DelegatingChatClient"/> it sees exactly one model round-trip per call and owns every
/// cross-cutting concern that used to be duplicated between the hand-written <c>LlmClient</c> and the
/// agent-only <c>AgentModelCallChatClient</c>:
/// <list type="bullet">
///   <item>the single per-call deadline (the SDK's own network timeout is disabled in the factory);</item>
///   <item>the completion-token cap, recomputed per call from the current prompt size;</item>
///   <item>building the <see cref="LlmCallResult"/> + mapping API/timeout/other failures onto it;</item>
///   <item>request-shape + DB logging via <see cref="IModelCallLogger"/> (call-log UI unchanged);</item>
///   <item>(optional) the per-step "thinking" progress line, and surfacing a failed call as a thrown,
///         run-ending error (<paramref name="throwOnFailure"/>).</item>
/// </list>
/// The streaming override is the pass-through used by the agent loop and by <c>LlmClient</c>'s text path;
/// the non-streaming override is a true single round-trip, used by structured output. The built result is
/// handed back through <paramref name="onCompleted"/> so a terminal consumer (LlmClient) can return it
/// without rebuilding; the agent path ignores it and reads the streamed text instead.
/// </summary>
public sealed class ModelCallLoggingChatClient : DelegatingChatClient
{
    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };

    private readonly AiModel _model;
    private readonly IModelCallLogger _logger;
    private readonly ModelCallLogContext _context;
    private readonly int _requestTimeoutSeconds;
    private readonly bool _throwOnFailure;
    private readonly Action<LlmCallResult>? _onCompleted;
    private readonly Action<string, string, string?>? _onProgress;
    private readonly int _maxSteps;
    private readonly int _hardCap;

    private int _step;

    public ModelCallLoggingChatClient(
        IChatClient inner, AiModel model, IModelCallLogger logger, ModelCallLogContext context,
        int requestTimeoutSeconds, bool throwOnFailure, Action<LlmCallResult>? onCompleted = null,
        Action<string, string, string?>? onProgress = null, int maxSteps = 0, int hardCap = 0) : base(inner)
    {
        _model = model;
        _logger = logger;
        _context = context;
        _requestTimeoutSeconds = requestTimeoutSeconds;
        _throwOnFailure = throwOnFailure;
        _onCompleted = onCompleted;
        _onProgress = onProgress;
        _maxSteps = maxSteps;
        _hardCap = hardCap;
        _step = context.FirstStep - 1;
    }

    /// <summary>
    /// Number of model calls made so far this run (auto-incremented per call). The agent orchestrator
    /// compares it against a phase's budget to tell a converged run (finished under budget) from one that
    /// exhausted its step budget.
    /// </summary>
    public int StepCount => _step;

    // True single round-trip — used by structured output (GetResponseAsync<T>) and any non-streaming caller.
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var call = Begin(messages, options);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_requestTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var response = await base.GetResponseAsync(call.Messages, call.Options, linkedCts.Token).ConfigureAwait(false);
            FinalizeSuccess(call.Result, call.Stopwatch, response.Text ?? string.Empty, response.FinishReason);
            await CompleteAsync(call.Result, call.Step).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller (e.g. app shutdown) cancelled — propagate so the worker treats it as a clean stop.
            throw;
        }
        catch (Exception ex)
        {
            await FailAsync(call.Result, call.Stopwatch, call.Step, ex).ConfigureAwait(false);
            if (_throwOnFailure)
                throw new InvalidOperationException($"LLM call failed: {call.Result.ErrorMessage}", ex);
            // Swallowed: hand back an empty response so a structured caller falls back to manual parsing
            // (the failure is recorded on the LlmCallResult delivered via onCompleted).
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = Begin(messages, options);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_requestTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var updates = new List<ChatResponseUpdate>();
        await using var enumerator = base.GetStreamingResponseAsync(call.Messages, call.Options, linkedCts.Token)
            .GetAsyncEnumerator(linkedCts.Token);

        // Enumerate manually so a streaming error is caught and logged here (yield must stay out of try).
        var failed = false;
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
                throw;
            }
            catch (Exception ex)
            {
                await FailAsync(call.Result, call.Stopwatch, call.Step, ex).ConfigureAwait(false);
                if (_throwOnFailure)
                    throw new InvalidOperationException($"LLM call failed: {call.Result.ErrorMessage}", ex);
                failed = true;
                break;
            }

            updates.Add(update);
            yield return update;
        }

        if (failed)
            yield break;

        var response = updates.ToChatResponse();
        FinalizeSuccess(call.Result, call.Stopwatch, response.Text ?? string.Empty, response.FinishReason);
        await CompleteAsync(call.Result, call.Step).ConfigureAwait(false);
    }

    // Per-call setup shared by both overrides: bump the step, size the request, build the log shape, and
    // emit the "thinking" progress line. Clones the options so the agent's shared instance is never mutated.
    private CallState Begin(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var step = ++_step;
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
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
        var callOptions = options?.Clone() ?? new ChatOptions();
        callOptions.MaxOutputTokens = maxTokens;
        result.RequestJson = BuildRequestJson(messageList, callOptions, maxTokens);

        _onProgress?.Invoke("thinking", $"Agent {_context.Agent.Name} đang suy nghĩ… (bước {BudgetLabel(step)})", null);
        return new CallState(step, messageList, callOptions, result, Stopwatch.StartNew());
    }

    private void FinalizeSuccess(LlmCallResult result, Stopwatch stopwatch, string text, ChatFinishReason? finishReason)
    {
        stopwatch.Stop();
        result.Content = text;
        result.ExtractedContent = text;
        result.ResponseText = text;
        result.DurationMs = stopwatch.ElapsedMilliseconds;
        result.HttpStatusCode = 200;
        result.IsSuccess = true;
        // finish_reason == "length" means the model hit its token cap mid-output (often truncated JSON);
        // flag it so a cut-off answer is distinguishable from a clean one.
        if (finishReason == ChatFinishReason.Length)
            result.ErrorMessage = "Phản hồi có thể bị cắt do đạt giới hạn token (finish_reason=length).";
        result.CompletionTokens = TokenEstimator.Estimate(text);
        result.TotalTokens = result.PromptTokens + result.CompletionTokens;
    }

    private async Task FailAsync(LlmCallResult result, Stopwatch stopwatch, int step, Exception ex)
    {
        stopwatch.Stop();
        result.DurationMs = stopwatch.ElapsedMilliseconds;
        result.IsSuccess = false;
        switch (ex)
        {
            // Non-2xx from the API (incl. OpenAI-compatible servers). Keep the short message in the
            // DB-persisted, UI-visible fields; the full exception is surfaced by the caller's logger.
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
        await CompleteAsync(result, step).ConfigureAwait(false);
    }

    // Persist the call log (one place, identical shape for both paths) then surface the result.
    private async Task CompleteAsync(LlmCallResult result, int step)
    {
        await _logger.LogAsync(_context.ProjectId, _context.Agent, result, step, _context.Purpose, _context.WorkflowRunId).ConfigureAwait(false);
        _onCompleted?.Invoke(result);
    }

    // Logged in the shape the call-log UI expects; tools are summarised by name (the full JSON schema is
    // produced downstream by the OpenAI SDK). The "thinking" field is injected by ThinkingDisabledHandler
    // in the HttpClient pipeline (see OpenAIChatClientFactory).
    private string BuildRequestJson(IList<ChatMessage> messageList, ChatOptions callOptions, int maxTokens) =>
        JsonSerializer.Serialize(new
        {
            model = _model.ModelId,
            messages = messageList.Select(m => new { role = m.Role.Value, content = m.Text }),
            temperature = callOptions.Temperature,
            max_tokens = maxTokens,
            stream = true,
            tools = callOptions.Tools?.Select(t => t.Name) ?? Enumerable.Empty<string>(),
            thinking = new { type = "disabled" }
        }, SerializeOptions);

    private string BudgetLabel(int step) =>
        _maxSteps <= 0 ? step.ToString()
        : step <= _maxSteps ? $"{step}/{_maxSteps}"
        : $"{step}/{_hardCap} (chạy thêm để hoàn tất)";

    private readonly record struct CallState(
        int Step, IList<ChatMessage> Messages, ChatOptions Options, LlmCallResult Result, Stopwatch Stopwatch);
}
