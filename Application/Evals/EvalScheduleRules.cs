namespace ICOGenerator.Application.Evals;

/// <summary>Biên hợp lệ dùng chung cho tạo/sửa lịch eval — một chỗ duy nhất để hai use case không lệch nhau.</summary>
public static class EvalScheduleRules
{
    /// <summary>Chu kỳ tối đa 720h (30 ngày) — dài hơn thế thì lịch không còn ý nghĩa "định kỳ".</summary>
    public const int MaxIntervalHours = 720;

    /// <summary>Ngưỡng tụt tối đa 4 điểm (thang 1–5 chỉ chênh được tối đa 4).</summary>
    public const double MaxRegressionThreshold = 4.0;

    public static bool IsValid(string? name, int intervalHours, double regressionThreshold) =>
        !string.IsNullOrWhiteSpace(name)
        && intervalHours is >= 1 and <= MaxIntervalHours
        && regressionThreshold is > 0 and <= MaxRegressionThreshold;
}
