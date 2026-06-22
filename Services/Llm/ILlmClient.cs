using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

public interface ILlmClient
{
    /// <param name="onToken">
    /// Optional callback invoked for each content delta as it streams in from the model. Lets callers
    /// surface the model "typing" live (e.g. push to the browser via SSE). The full content is still
    /// returned in the result; passing null keeps the call buffered as before.
    /// </param>
    Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessageDto> messages, double temperature, Action<string>? onToken = null, CancellationToken cancellationToken = default);
}
