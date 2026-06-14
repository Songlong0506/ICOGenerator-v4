namespace ICOGenerator.Services.Llm;

/// <summary>
/// Heuristic token estimator (~4 chars per token) shared across services that
/// record approximate token usage without invoking a real tokenizer.
/// </summary>
public static class TokenEstimator
{
    public static int Estimate(string? text)
        => string.IsNullOrWhiteSpace(text) ? 0 : Math.Max(1, text.Length / 4);
}
