using System.Text;
using System.Text.Json;

namespace ICOGenerator.Services.Evals;

/// <summary>
/// Dựng lại đầu vào của một lượt trọng tài từ <c>AgentModelCallLog.RequestJson</c> (bản preview mà
/// <c>ModelCallRequestPreview</c> ghi: <c>{"messages":[{"role","content"}]}</c>, <c>content</c> là chuỗi khi
/// message toàn chữ, mảng part khi có ảnh).
///
/// <para>
/// Chỉ nhận đúng hình dạng của một lượt trọng tài GỐC: một system + một user, toàn chữ. Dòng log của lần
/// <b>đòi lại JSON</b> (<c>LlmClient.ChatStructuredAsync</c> nối thêm lượt assistant hỏng + lời đòi) bị từ
/// chối: nó là cùng một lượt với dòng gốc ngay trước, đếm cả hai là đếm đôi lượt đó.
/// </para>
/// </summary>
public static class JudgeLogRequest
{
    public sealed record Input(string System, string User);

    public static Input? TryParse(string? requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                return null;

            string? system = null, user = null;
            foreach (var message in messages.EnumerateArray())
            {
                var role = message.TryGetProperty("role", out var r) ? r.GetString() : null;
                var text = message.TryGetProperty("content", out var c) ? TextOf(c) : null;
                if (text == null)
                    return null;

                switch (role)
                {
                    case "system" when system == null:
                        system = text;
                        break;
                    case "user" when user == null:
                        user = text;
                        break;
                    default:
                        return null; // assistant (lần đòi lại), system/user thứ hai: không phải một lượt gốc.
                }
            }

            return system != null && user != null ? new Input(system, user) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Chuỗi ⇒ chính nó; mảng part chỉ-chữ ⇒ ghép lại; có part không phải chữ (ảnh) ⇒ null — bytes ảnh không
    // nằm trong log, chạy lại sẽ là một đầu vào khác.
    private static string? TextOf(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (!part.TryGetProperty("type", out var type) || type.GetString() != "text"
                || !part.TryGetProperty("text", out var text))
                return null;
            sb.Append(text.GetString());
        }
        return sb.ToString();
    }
}
