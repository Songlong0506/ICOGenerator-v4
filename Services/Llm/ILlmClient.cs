using ICOGenerator.Domain;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

public interface ILlmClient
{
    /// <summary>
    /// Streams one chat completion, buffering the full text into the returned result. The call is logged to
    /// the DB (via the shared <see cref="ModelCallLoggingChatClient"/> middleware) using <paramref name="logContext"/>,
    /// so callers no longer log separately.
    /// </summary>
    /// <param name="onToken">
    /// Optional callback invoked for each content delta as it streams in from the model. Lets callers
    /// surface the model "typing" live (e.g. push to the browser via SSE). The full content is still
    /// returned in the result; passing null keeps the call buffered as before.
    /// </param>
    Task<LlmCallResult> ChatWithLogAsync(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="ChatWithLogAsync"/> but asks the model for structured output (a JSON object matching
    /// <typeparamref name="T"/>) when <see cref="StructuredOutputPolicy"/> opts the model in, and returns the
    /// deserialized value alongside the raw result. Falls back transparently to the plain streaming text call
    /// when the model is not opted in, so <paramref name="onToken"/> still streams on that path.
    /// </summary>
    /// <returns>
    /// The raw call result plus the deserialized value, or <c>null</c> value when structured output was not
    /// used / not available / not parseable — in which case the caller parses <see cref="LlmCallResult.Content"/>
    /// with its existing parser.
    /// </returns>
    Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class;
}
