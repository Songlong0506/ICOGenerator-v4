using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Dựng phần "Response" của call log cho một lượt có gọi tool: chữ của model (nếu có) rồi từng lời gọi tool
/// kèm tham số. Không có nó thì mọi bước agent chỉ gọi tool (SetPocContent, AuditPocContent…) hiện Response
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

    public static string Append(string text, IEnumerable<ChatMessage> messages)
    {
        var calls = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        if (calls.Count == 0)
            return text;

        var sb = new StringBuilder(text);
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
