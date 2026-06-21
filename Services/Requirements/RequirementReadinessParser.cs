using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Requirements;

// Biến raw text của cổng kiểm tra thành (Ready + Message + Suggestions). Gate được nhắc trả JSON
// {"ready": true|false, "message": "...", "suggestions": [...]}. Model local không phải lúc nào cũng
// tuân thủ, nên parser fail-open: không đọc được cờ thì coi như Ready=true để khỏi chặn cứng luồng.
public class RequirementReadinessParser
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Tái dùng cho phần message + suggestions (đã làm sạch chip) để đồng nhất với lượt chat BA.
    private readonly BAChatReplyParser _replyParser;

    public RequirementReadinessParser(BAChatReplyParser replyParser)
    {
        _replyParser = replyParser;
    }

    public RequirementReadiness Parse(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
            return RequirementReadiness.ProceedDefault;

        bool? ready = null;
        try
        {
            var json = JsonExtractor.Extract(text);
            if (!string.IsNullOrEmpty(json))
            {
                var parsed = JsonSerializer.Deserialize<RawReadiness>(json, JsonOptions);
                ready = parsed?.Ready;
            }
        }
        catch
        {
            // JSON hỏng → fail-open bên dưới.
        }

        // Không đọc được cờ → cho qua (fail-open) để không chặn việc sinh tài liệu vì lỗi parse.
        if (ready != false)
            return new RequirementReadiness { Ready = true };

        // Chưa đủ: lấy message + suggestions để đẩy vào khung chat cho người dùng trả lời.
        var reply = _replyParser.Parse(text);
        return new RequirementReadiness
        {
            Ready = false,
            Message = reply.Message,
            Suggestions = reply.Suggestions
        };
    }

    private class RawReadiness
    {
        public bool? Ready { get; set; }
    }
}
