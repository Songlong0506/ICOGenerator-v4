using ICOGenerator.Domain;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

public interface ILlmClient
{
    /// <param name="onToken">
    /// Optional callback invoked for each content delta as it streams in from the model. Lets callers
    /// surface the model "typing" live (e.g. push to the browser via SSE). The full content is still
    /// returned in the result; passing null keeps the call buffered as before.
    /// </param>
    Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessageDto> messages, double temperature, Action<string>? onToken = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Native function-calling variant: sends the conversation plus the tool set (advertised to the
    /// model via the OpenAI "tools" parameter) and returns the model's reply — the assistant message(s)
    /// and any requested tool calls — WITHOUT invoking the tools. The caller runs the tool loop. Buffered
    /// (non-streaming) so the structured tool calls arrive complete. Pass an empty tool set to force a
    /// plain, tool-free completion (used for the final "summarise what you did" turn).
    /// </summary>
    Task<LlmToolCallResult> ChatWithToolsAsync(AiModel model, IList<ChatMessage> messages, IList<AITool> tools, double temperature, CancellationToken cancellationToken = default);
}
