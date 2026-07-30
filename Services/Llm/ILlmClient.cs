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
    /// Like <see cref="ChatWithLogAsync"/> but asks the model for JSON and returns the deserialized value
    /// alongside the raw result. How hard it asks depends on <see cref="AiModel.StructuredOutputMode"/>:
    /// <list type="bullet">
    ///   <item><b>None</b> — no <c>response_format</c> at all; identical to <see cref="ChatWithLogAsync"/> with a
    ///         null value handed back, so the caller parses the text itself.</item>
    ///   <item><b>JsonObject</b> — <c>response_format: json_object</c> on the streaming path, so
    ///         <paramref name="onToken"/> keeps working and the reply is guaranteed to be syntactically valid JSON.</item>
    ///   <item><b>JsonSchema</b> — a schema derived from <typeparamref name="T"/>. Non-streaming, so
    ///         <paramref name="onToken"/> is NOT called at this level.</item>
    /// </list>
    /// An endpoint that rejects the requested <c>response_format</c> is retried once on the plain text path
    /// rather than failing the turn.
    /// </summary>
    /// <returns>
    /// The raw call result plus the deserialized value, or <c>null</c> value when structured output was not
    /// used / not available / not parseable — in which case the caller parses <see cref="LlmCallResult.Content"/>
    /// with its existing parser.
    /// </returns>
    Task<(LlmCallResult Result, T? Value)> ChatStructuredAsync<T>(AiModel model, List<ChatMessage> messages, double temperature, ModelCallLogContext logContext, Action<string>? onToken = null, CancellationToken cancellationToken = default) where T : class;
}
