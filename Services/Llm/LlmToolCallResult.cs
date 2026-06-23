using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Outcome of a single tool-enabled model call (native function-calling path). Wraps the
/// <see cref="LlmCallResult"/> used for cost/logging, plus the model's reply: the assistant
/// message(s) to append to the running history and any tool calls the model requested. The caller
/// runs the tool loop, so this type carries the calls without invoking them.
/// </summary>
public sealed class LlmToolCallResult
{
    public required LlmCallResult Call { get; init; }

    /// <summary>
    /// The assistant message(s) returned by the model, to append to the conversation before sending
    /// tool results back so the next request stays valid. Empty when the call failed.
    /// </summary>
    public IReadOnlyList<ChatMessage> ResponseMessages { get; init; } = [];

    /// <summary>
    /// Tool calls the model requested this turn. Empty means the model produced a final answer
    /// (with native tool-calling, a plain reply is a legitimate "done").
    /// </summary>
    public IReadOnlyList<FunctionCallContent> FunctionCalls { get; init; } = [];

    /// <summary>The model's text this turn (the final answer when there are no tool calls).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// True when the model stopped on finish_reason=length (hit the token cap). On a tool-call turn
    /// this usually means the arguments JSON was cut off mid-stream, so a <see cref="FunctionCalls"/>
    /// entry can arrive with missing/empty arguments — the caller should treat such a call as
    /// incomplete rather than run it.
    /// </summary>
    public bool Truncated { get; init; }
}
