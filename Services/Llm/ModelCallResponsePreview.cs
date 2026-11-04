using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Nội dung tab <b>Response</b> của call log. Lượt agent thường KHÔNG viết chữ nào — toàn bộ output là tool call
/// (SetPocContent, AppendPocContent…) — nên chỉ ghi <c>response.Text</c> thì một lượt 60k token output hiện ra
/// khung trống, nhìn y như model không trả gì hay tham số gửi sai. Ghi thêm từng tool call (tên + đối số, cắt
/// ngắn) để thấy model đã làm gì và call nào bị cụt.
/// </summary>
public static class ModelCallResponsePreview
{
    // Đối số một call có thể là cả trang HTML: cắt để log không phình, đủ để nhận ra call đó là gì.
    internal const int MaxArgumentChars = 2000;

    /// <param name="thinkingBlocks">Số block thinking Claude đã sinh (nội dung không trả về, xem AnthropicVertexChatClient).</param>
    /// <param name="reasoningTokens">Số token suy luận provider báo (OpenAI <c>reasoning_tokens</c>), 0 nếu không có.</param>
    public static string Build(string text, IReadOnlyList<FunctionCallContent> calls, int thinkingBlocks = 0, int reasoningTokens = 0)
    {
        var sb = new StringBuilder();
        // Đứng ĐẦU: một lượt tiêu hết output vào suy luận ẩn là lượt "Response trống" — dòng này là câu trả lời
        // cho câu hỏi đầu tiên người ta đặt ra khi mở log ("model có trả gì không?").
        if (thinkingBlocks > 0 || reasoningTokens > 0)
        {
            sb.Append("[Model đã suy luận ẩn");
            if (reasoningTokens > 0)
                sb.Append($" {reasoningTokens:N0} token");
            if (thinkingBlocks > 0)
                sb.Append($" ({thinkingBlocks} block thinking)");
            sb.Append(" — nội dung suy luận không được trả về nhưng vẫn tính vào token output; chỉnh ở \"Reasoning effort\" của model]");
        }
        if (text.Length > 0)
        {
            if (sb.Length > 0)
                sb.Append("\n\n");
            sb.Append(text);
        }

        foreach (var call in calls)
        {
            if (sb.Length > 0)
                sb.Append("\n\n");
            var args = call.Arguments is { Count: > 0 } a ? JsonSerializer.Serialize(a, AIJsonUtilities.DefaultOptions) : "{}";
            sb.Append("→ tool call ").Append(call.Name);
            if (call.Arguments is not { Count: > 0 })
                sb.Append(" — KHÔNG có đối số (nhiều khả năng bị cắt ở trần token output)");
            sb.Append('\n').Append(args.Length <= MaxArgumentChars
                ? args
                : args[..MaxArgumentChars] + $"…[cắt bớt, tổng {args.Length:N0} ký tự]");
        }
        return sb.ToString();
    }
}
