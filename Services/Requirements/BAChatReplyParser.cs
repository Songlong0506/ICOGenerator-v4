using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Requirements;

// Biến raw text BA trả về thành (Message + Suggestions). BA được nhắc trả JSON
// {"message": "...", "suggestions": ["...", ...]} để UI render chip chọn nhanh (giống plan mode).
// Mô hình local không phải lúc nào cũng tuân thủ JSON, nên parser luôn fallback an toàn về text thuần
// (không chip) — đúng bằng hành vi cũ — thay vì ném lỗi.
public class BAChatReplyParser
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Giữ số chip vừa phải để không tràn UI, và bỏ "gợi ý" quá dài (model lỡ nhét cả đoạn văn).
    private const int MaxSuggestions = 6;
    private const int MaxSuggestionLength = 200;

    public BAChatReply Parse(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
            return new BAChatReply();

        try
        {
            var json = JsonExtractor.Extract(text);
            if (!string.IsNullOrEmpty(json))
            {
                var parsed = JsonSerializer.Deserialize<RawReply>(json, JsonOptions);
                if (parsed != null)
                {
                    var message = (parsed.Message ?? string.Empty).Trim();
                    var suggestions = CleanSuggestions(parsed.Suggestions);

                    // Có cấu trúc rõ ràng (message hoặc suggestions) → dùng kết quả parse.
                    if (message.Length > 0 || suggestions.Count > 0)
                    {
                        return new BAChatReply
                        {
                            Message = message.Length > 0
                                ? message
                                : "Đã ghi nhận. Bạn có thể chọn một gợi ý bên dưới hoặc tự nhập thêm.",
                            Suggestions = suggestions
                        };
                    }
                }
            }
        }
        catch
        {
            // JSON hỏng/không đúng dạng: rơi xuống fallback text thuần bên dưới.
        }

        // Fallback: coi toàn bộ phản hồi là text hiển thị, không kèm chip (giống hành vi trước đây).
        return new BAChatReply { Message = text };
    }

    private static List<string> CleanSuggestions(List<JsonElement>? raw)
    {
        var result = new List<string>();
        if (raw == null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in raw)
        {
            var value = ExtractText(element);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            value = value.Trim();
            if (value.Length > MaxSuggestionLength || !seen.Add(value))
                continue;

            result.Add(value);
            if (result.Count >= MaxSuggestions)
                break;
        }

        return result;
    }

    // Chấp nhận cả ["a","b"] lẫn [{"label":"a"},{"text":"b"}] để bền với cách model trả khác nhau.
    private static string? ExtractText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.ToString(),
        JsonValueKind.Object => FirstStringProperty(element, "label", "text", "value", "title", "option"),
        _ => null
    };

    private static string? FirstStringProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        return null;
    }

    private class RawReply
    {
        public string? Message { get; set; }
        public List<JsonElement>? Suggestions { get; set; }
    }
}
