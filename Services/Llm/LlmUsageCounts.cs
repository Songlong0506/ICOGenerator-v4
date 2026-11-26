namespace ICOGenerator.Services.Llm;

/// <summary>
/// Khoá trong <c>UsageDetails.AdditionalCounts</c> cho những con số mà <c>Microsoft.Extensions.AI</c> chưa có
/// thuộc tính riêng. Client nào biết con số thì ghi vào đúng khoá này, <see cref="ModelCallLoggingChatClient"/>
/// đọc ra ghi log — hai đầu dùng chung một hằng để một lần gõ sai tên không làm số rơi về 0 âm thầm.
/// </summary>
public static class LlmUsageCounts
{
    /// <summary>
    /// Số token prompt provider vừa GHI vào cache ở lời gọi này (Anthropic: <c>cache_creation_input_tokens</c>).
    /// Nằm TRONG <c>InputTokenCount</c> và tách rời với <c>CachedInputTokenCount</c> (phần ĐỌC từ cache).
    /// </summary>
    public const string CacheWriteInputTokens = "CacheWriteInputTokenCount";
}
