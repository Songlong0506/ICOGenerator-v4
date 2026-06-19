namespace ICOGenerator.Services.Llm;

/// <summary>
/// Heuristic token estimator (~4 chars per token); approximate, not a real tokenizer.
/// </summary>
public static class TokenEstimator
{
    public static int Estimate(string? text)
        => string.IsNullOrWhiteSpace(text) ? 0 : Math.Max(1, text.Length / 4);
}
