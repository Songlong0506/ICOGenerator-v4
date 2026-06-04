namespace ICOGenerator.Services.Common;

public static class TokenEstimator
{
    public static int Estimate(string? text)
        => string.IsNullOrWhiteSpace(text) ? 0 : Math.Max(1, text.Length / 4);
}
