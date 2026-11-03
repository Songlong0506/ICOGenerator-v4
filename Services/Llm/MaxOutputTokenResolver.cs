using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Resolves the completion-token cap for one model call. A fixed 100k overflows small-context models, so
/// the cap is shrunk to what's left in the context window after the (estimated) prompt. Used by the shared
/// per-call middleware (<see cref="ModelCallLoggingChatClient"/>) for every path, so all requests are sized
/// identically.
/// <para>
/// <see cref="AiModel.MaxOutputTokens"/> (khai ở màn hình AI Models) là trần cứng đặt lên trên: endpoint đứng sau
/// gateway có ngưỡng thời gian thì một lượt dài hơn ngưỡng đó chết hẳn — model nhanh tới đâu cũng chỉ sinh được
/// chừng ấy token trước khi bị cắt — nên trần theo model phải thắng mọi con số tính từ context window.
/// </para>
/// </summary>
public static class MaxOutputTokenResolver
{
    private const int MaxCompletionTokens = 100000;
    private const int ContextSafetyMargin = 1024;

    public static int Resolve(AiModel model, int promptTokens)
    {
        var ceiling = model.MaxOutputTokens > 0 ? Math.Min(model.MaxOutputTokens, MaxCompletionTokens) : MaxCompletionTokens;
        if (model.ContextWindow <= 0)
            return ceiling;

        var available = model.ContextWindow - promptTokens - ContextSafetyMargin;
        return Math.Clamp(available, Math.Min(256, ceiling), ceiling);
    }
}
