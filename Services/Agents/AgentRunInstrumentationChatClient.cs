using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Logging;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Agents;

/// <summary>
/// Sits directly beneath MAF's function-invocation layer so it observes every underlying model round of
/// an agent run (not the run as a whole — verified by the middleware ordering). It reproduces the
/// per-round cross-cutting work the old hand-rolled native loop did inline, so moving the loop itself
/// into MAF leaves observable behaviour unchanged:
///   • emits an <c>onProgress("thinking", …)</c> event before each round,
///   • writes one <see cref="AgentModelCallLog"/> per round (success or failure) for the call-log UI, and
///   • caps <see cref="ChatOptions.MaxOutputTokens"/> per round to what's left in the context window
///     (small-context safety), using the same <see cref="LlmClient.ResolveMaxTokens"/> logic as the
///     buffered <see cref="ILlmClient"/> path.
/// A failure in the audit log must never abort the run, so logging is best-effort.
/// </summary>
internal sealed class AgentRunInstrumentationChatClient : DelegatingChatClient
{
    private static readonly JsonSerializerOptions RequestLogOptions = new() { WriteIndented = true };

    private readonly IModelCallLogger _modelCallLogger;
    private readonly AiModel _model;
    private readonly Agent _agent;
    private readonly Guid _projectId;
    private readonly Guid? _workflowRunId;
    private readonly int _maxSteps;
    private readonly double _temperature;
    private readonly Action<string, string, string?>? _onProgress;
    private int _step;

    public AgentRunInstrumentationChatClient(
        IChatClient innerClient, IModelCallLogger modelCallLogger, AiModel model, Agent agent,
        Guid projectId, Guid? workflowRunId, int maxSteps, double temperature,
        Action<string, string, string?>? onProgress) : base(innerClient)
    {
        _modelCallLogger = modelCallLogger;
        _model = model;
        _agent = agent;
        _projectId = projectId;
        _workflowRunId = workflowRunId;
        _maxSteps = maxSteps;
        _temperature = temperature;
        _onProgress = onProgress;
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var step = ++_step;
        _onProgress?.Invoke("thinking", $"Agent {_agent.Name} đang suy nghĩ… (bước {step}/{_maxSteps})", null);

        var promptTokens = TokenEstimator.Estimate(string.Join("\n", messageList.Select(m => m.Text)));
        // MAF reuses one ChatOptions per run; recompute the per-round cap from this round's actual prompt.
        if (options is not null)
            options.MaxOutputTokens = LlmClient.ResolveMaxTokens(_model, promptTokens);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await base.GetResponseAsync(messageList, options, cancellationToken);
            stopwatch.Stop();
            await LogAsync(step, BuildSuccess(messageList, options, response, promptTokens, stopwatch.ElapsedMilliseconds));
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller (e.g. app shutdown) cancelled — propagate so the worker treats it as a clean stop.
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            // Log the failed round too (the old loop logged failed calls), then let it surface.
            await LogAsync(step, BuildFailure(messageList, options, ex, promptTokens, stopwatch.ElapsedMilliseconds));
            throw;
        }
    }

    private async Task LogAsync(int step, LlmCallResult call)
    {
        try
        {
            await _modelCallLogger.LogAsync(_projectId, _agent, call, step, "AgentRun", _workflowRunId);
        }
        catch
        {
            // A logging failure must never abort the agent run.
        }
    }

    private LlmCallResult BuildSuccess(IReadOnlyList<ChatMessage> messages, ChatOptions? options, ChatResponse response, int promptTokens, long durationMs)
    {
        var text = response.Text ?? string.Empty;
        var completionTokens = TokenEstimator.Estimate(text);
        return new LlmCallResult
        {
            Endpoint = _model.Endpoint,
            ModelId = _model.ModelId,
            ModelName = _model.Name,
            RequestJson = BuildRequestJson(messages, options),
            Content = text,
            ExtractedContent = text,
            ResponseText = text,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            DurationMs = durationMs,
            HttpStatusCode = 200,
            IsSuccess = true,
            // finish_reason == "length" means the model hit its token cap mid-output (often truncated JSON).
            ErrorMessage = response.FinishReason == ChatFinishReason.Length
                ? "Phản hồi có thể bị cắt do đạt giới hạn token (finish_reason=length)."
                : null
        };
    }

    private LlmCallResult BuildFailure(IReadOnlyList<ChatMessage> messages, ChatOptions? options, Exception ex, int promptTokens, long durationMs)
    {
        var status = ex is ClientResultException clientResult ? clientResult.Status : (int?)null;
        return new LlmCallResult
        {
            Endpoint = _model.Endpoint,
            ModelId = _model.ModelId,
            ModelName = _model.Name,
            RequestJson = BuildRequestJson(messages, options),
            Content = status is { } s ? $"API error: {s}\n\n{ex.Message}" : ex.Message,
            ResponseText = ex.Message,
            ErrorMessage = status is { } code ? $"API error: {code}" : ex.Message,
            PromptTokens = promptTokens,
            DurationMs = durationMs,
            HttpStatusCode = status,
            IsSuccess = false
        };
    }

    // Mirrors the request shape the call-log UI expects (same fields the buffered ILlmClient path logged);
    // the real wire request is produced downstream by the OpenAI SDK.
    private string BuildRequestJson(IReadOnlyList<ChatMessage> messages, ChatOptions? options) =>
        JsonSerializer.Serialize(new
        {
            model = _model.ModelId,
            messages = messages.Select(m => new { role = m.Role.Value, content = m.Text }),
            temperature = _temperature,
            max_tokens = options?.MaxOutputTokens,
            tools = options?.Tools?.Select(t => t.Name) ?? Enumerable.Empty<string>(),
            thinking = new { type = "disabled" }
        }, RequestLogOptions);
}
