using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Resolves the completion-token cap for one model call. A fixed 100k overflows small-context models, so
/// the cap is shrunk to what's left in the context window after the (estimated) prompt. Used by the shared
/// per-call middleware (<see cref="ModelCallLoggingChatClient"/>) for every path, so all requests are sized
/// identically.
/// </summary>
public static class MaxOutputTokenResolver
{
    // Upper bound for completion tokens, plus prompt headroom so small-context models aren't asked for
    // more output than fits.
    private const int MaxCompletionTokens = 100000;
    private const int ContextSafetyMargin = 1024;

    public static int Resolve(AiModel model, int promptTokens)
    {
        if (model.ContextWindow <= 0)
            return MaxCompletionTokens;

        var available = model.ContextWindow - promptTokens - ContextSafetyMargin;
        return Math.Clamp(available, 256, MaxCompletionTokens);
    }
}
