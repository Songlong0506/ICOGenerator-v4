using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Dựng phần "Response" của call log cho một lượt có gọi tool hoặc có suy luận ẩn: chữ của model (nếu có),
/// phần suy luận ẩn (nếu endpoint trả ra), rồi từng lời gọi tool kèm tham số. Không có nó thì mọi bước agent chỉ gọi tool (SetPocContent, AuditPocContent…) hiện Response
/// TRỐNG trong popup AI Call Logs — trông như bước đó không làm gì, dù chính nó viết ra phần lớn sản phẩm.
/// </summary>
public static class ToolCallLog
{
    // Một tham số dài (cả vùng HTML của POC) vẫn được giữ đủ để đọc hiểu, nhưng không để một lời gọi đơn
    // lẻ thổi phồng bảng log: phần thật đã nằm trên đĩa.
    private const int MaxArgumentChars = 20_000;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Phần suy luận ẩn chỉ cần đủ để biết model đang nghĩ gì — không chép cả chục nghìn token vào bảng log.
    private const int MaxReasoningChars = 4_000;

    public static string Append(string text, IEnumerable<ChatMessage> messages)
    {
        var contents = messages.SelectMany(m => m.Contents).ToList();
        var calls = contents.OfType<FunctionCallContent>().ToList();
        // Suy luận ẩn (khi endpoint chịu trả nó ra): một lượt "dùng hết token mà không có chữ nào" mà log chỉ
        // hiện Response trống thì không ai biết token đã đi đâu.
        var reasoning = string.Concat(contents.OfType<TextReasoningContent>().Select(r => r.Text));
        if (calls.Count == 0 && reasoning.Length == 0)
            return text;

        var sb = new StringBuilder(text);
        if (reasoning.Length > 0)
        {
            if (sb.Length > 0)
                sb.AppendLine().AppendLine();
            sb.Append($"→ Suy luận ẩn của model ({reasoning.Length} ký tự):").AppendLine();
            sb.Append(reasoning.Length <= MaxReasoningChars ? reasoning : reasoning[..MaxReasoningChars] + "… (cắt bớt)");
        }
        foreach (var call in calls)
        {
            if (sb.Length > 0)
                sb.AppendLine().AppendLine();
            sb.Append("→ Gọi tool ").Append(call.Name);
            if (call.Arguments is not { Count: > 0 } args)
            {
                sb.Append("()");
                continue;
            }

            sb.AppendLine(":");
            var shown = args.ToDictionary(a => a.Key, a => Shorten(a.Value));
            sb.Append(JsonSerializer.Serialize(shown, Options));
        }
        return sb.ToString();
    }

    private static object? Shorten(object? value)
    {
        var text = value switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            _ => null
        };
        if (text == null)
            return value;
        return text.Length <= MaxArgumentChars
            ? text
            : text[..MaxArgumentChars] + $"… (cắt bớt — tổng {text.Length} ký tự)";
    }
}
