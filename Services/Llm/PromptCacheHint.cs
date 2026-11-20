using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Đánh dấu "từ đầu prompt tới hết message này là phần TĨNH" — gợi ý cho client nào phải xin prompt cache
/// TƯỜNG MINH (Anthropic: <c>cache_control</c>) biết đặt điểm cắt ở đâu.
///
/// <para>
/// Vì sao cần một dấu do CALLER đặt: OpenAI tự cache theo prefix nên chỉ cần xếp khối tĩnh lên trước
/// (xem <c>BAChatService.BuildMessagesAsync</c>), còn Anthropic chỉ cache ở đúng chỗ được xin, và chỉ ở
/// RANH GIỚI KHỐI. Client không biết khối system nào tĩnh, khối nào đổi mỗi lượt — chỉ người lắp prompt
/// biết. Không có dấu thì client đoán (xem <see cref="AnthropicVertexChatClient"/>).
/// </para>
///
/// <para>
/// Dấu nằm trong <see cref="ChatMessage.AdditionalProperties"/>; đường OpenAI không đưa trường đó lên dây
/// (<c>StructuredOutputWireFormatTests.CacheBreakpointMark_NeverReachesTheOpenAiWire</c> giữ điều này).
/// </para>
/// </summary>
public static class PromptCacheHint
{
    /// <summary>Khoá trong <see cref="ChatMessage.AdditionalProperties"/>.</summary>
    public const string BreakpointKey = "ico.cache_breakpoint";

    /// <summary>Đánh dấu message là điểm kết thúc của một đoạn prefix tĩnh. Trả lại chính message.</summary>
    public static ChatMessage AsCacheBreakpoint(this ChatMessage message)
    {
        (message.AdditionalProperties ??= new AdditionalPropertiesDictionary())[BreakpointKey] = true;
        return message;
    }

    public static bool IsCacheBreakpoint(this ChatMessage message)
        => message.AdditionalProperties?.TryGetValue(BreakpointKey, out var value) == true && value is true;
}
