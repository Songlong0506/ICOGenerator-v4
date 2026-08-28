using System.Text;
using System.Text.Json;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Mọi thứ liên quan tới việc ĐỌC JSON do model trả về, gom một chỗ (trước đây rải ở hai static class
/// riêng cộng với một hàm <c>ParseFallback</c> chép tay ở gần chục service).
/// <list type="bullet">
///   <item><see cref="Options"/> — model không đảm bảo đúng hoa/thường tên field nên luôn so khớp
///         không phân biệt hoa thường.</item>
///   <item><see cref="ExtractObject"/> — bóc object JSON ra khỏi hàng rào ```json / văn dẫn quanh nó.</item>
///   <item><see cref="TryDeserialize{T}"/> — bóc + đọc vào <typeparamref name="T"/>, KHÔNG NÉM: mọi thứ
///         không đọc được trả <c>null</c>, tức "caller tự lo" (fallback text thuần / danh sách rỗng).</item>
/// </list>
/// </summary>
public static class LlmJson
{
    /// <summary>Tùy chọn đọc dùng chung cho mọi parser đọc JSON model trả về.</summary>
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Bóc object JSON đầu tiên (cân bằng ngoặc) ra khỏi phản hồi, bỏ hàng rào ```json và văn dẫn quanh nó.
    /// Trả chuỗi RỖNG khi không có <c>'{'</c> (model trả văn xuôi) hoặc ngoặc không cân (phản hồi bị cắt
    /// giữa chừng) — caller coi rỗng là "không có JSON ở đây" thay vì nhận nửa object.
    /// </summary>
    public static string ExtractObject(string? text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.StartsWith("```"))
        {
            var firstNewLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
                text = text.Substring(firstNewLine + 1, lastFence - firstNewLine - 1).Trim();
        }

        var start = text.IndexOf('{');
        if (start < 0) return string.Empty;

        int depth = 0; bool inString = false; bool escape = false;
        for (int i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            if (c == '}') depth--;
            if (depth == 0) return text.Substring(start, i - start + 1);
        }
        return string.Empty;
    }

    /// <summary>
    /// Bóc JSON khỏi <paramref name="text"/> rồi đọc vào <typeparamref name="T"/>. Trả <c>null</c> cho mọi
    /// trường hợp không dùng được (không có JSON, JSON hỏng, kiểu không khớp) — đây là điểm chung của mọi
    /// đường "structured output không có/không dùng được thì parse tay".
    /// </summary>
    /// <param name="requireKnownProperty">
    /// Bật khi phản hồi được coi là ĐÃ có cấu trúc (structured output): System.Text.Json vui vẻ biến một
    /// object toàn field lạ thành <typeparamref name="T"/> mặc-định-hết, trông y như parse thành công và
    /// âm thầm cướp mất parser dự phòng của caller. Bật cờ này thì phản hồi phải trùng ÍT NHẤT MỘT tên
    /// field với <typeparamref name="T"/> mới được tin.
    /// </param>
    public static T? TryDeserialize<T>(string? text, bool requireKnownProperty = false) where T : class
    {
        var json = ExtractObject(text);
        if (json.Length == 0)
            return null;

        try
        {
            return Read<T>(json, requireKnownProperty);
        }
        catch (JsonException)
        {
            // JSON hỏng ở tầng DÃY THOÁT — model nhả `\u1E1y` (ba chữ số hex rồi tới chữ cái) giữa một
            // câu tiếng Việt. Sửa rồi đọc lại; sửa không xong thì vẫn null như trước.
            var repaired = RepairStringEscapes(json);
            if (ReferenceEquals(repaired, json))
                return null;

            try
            {
                return Read<T>(repaired, requireKnownProperty);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private static T? Read<T>(string json, bool requireKnownProperty) where T : class
    {
        if (requireKnownProperty && !SharesAnyPropertyWith<T>(json))
            return null;

        return JsonSerializer.Deserialize<T>(json, Options);
    }

    /// <summary>
    /// Sửa các DÃY THOÁT hỏng bên trong chuỗi JSON, chỉ chạy khi lần đọc đầu đã ném. Trả về CHÍNH
    /// <paramref name="json"/> (so bằng tham chiếu) khi không sửa gì — caller dùng đó để biết "không có
    /// gì để thử lại" thay vì gọi deserialize thêm một lần vô ích.
    ///
    /// <para>
    /// Vì sao phải có: model viết tiếng Việt hay nhả JSON dạng ASCII toàn `\uXXXX`, và chỉ cần MỘT dãy
    /// rụng một chữ số hex là cả object không đọc được. Ca thật (dự án JD Libary, lượt 6): `\u1E1y` trong
    /// chữ "vậy" ⇒ <see cref="BAChatReplyParser"/> rơi về nhánh text thuần và **nguyên khối JSON** —
    /// `{"message":"C\u1EA3m \u01A1n…","suggestions":[…],"ready":false}` — lên màn hình người dùng như
    /// một lượt trả lời của BA.
    /// </para>
    ///
    /// <para>
    /// Sửa theo hướng MẤT KÝ TỰ chứ không đoán ký tự: một dãy `\u` hỏng bị bỏ hẳn (kèm tối đa 3 chữ số
    /// hex đi cùng), một dấu `\` đứng trước ký tự không phải escape hợp lệ thì bỏ dấu `\`. Đoán bừa chữ
    /// mà model định viết là bịa nội dung nghiệp vụ; mất một chữ trong một câu vẫn là một lượt chat đọc
    /// được, còn dựng sai một chữ thì không ai biết đường mà lần.
    /// </para>
    /// </summary>
    private static string RepairStringEscapes(string json)
    {
        const string SimpleEscapes = "\"\\/bfnrt";

        var sb = new StringBuilder(json.Length);
        var inString = false;
        var repaired = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];

            if (!inString)
            {
                sb.Append(c);
                if (c == '"') inString = true;
                continue;
            }

            if (c == '"')
            {
                sb.Append(c);
                inString = false;
                continue;
            }

            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            if (i + 1 >= json.Length)
            {
                repaired = true; // dấu `\` cụt ở cuối chuỗi
                continue;
            }

            var next = json[i + 1];

            if (next == 'u')
            {
                if (i + 5 < json.Length && IsHex(json[i + 2]) && IsHex(json[i + 3]) && IsHex(json[i + 4]) && IsHex(json[i + 5]))
                {
                    sb.Append(json, i, 6);
                    i += 5;
                    continue;
                }

                // `\u` thiếu chữ số: bỏ cả dãy, kể cả các chữ số hex lỡ dở đi kèm.
                var end = i + 2;
                var limit = Math.Min(json.Length, end + 3);
                while (end < limit && IsHex(json[end]))
                    end++;
                i = end - 1;
                repaired = true;
                continue;
            }

            if (SimpleEscapes.IndexOf(next) >= 0)
            {
                sb.Append(c).Append(next);
                i++;
                continue;
            }

            sb.Append(next); // escape lạ (`\x`): giữ ký tự, bỏ dấu gạch chéo
            i++;
            repaired = true;
        }

        return repaired ? sb.ToString() : json;
    }

    private static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool SharesAnyPropertyWith<T>(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return false;

        var expected = typeof(T)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return document.RootElement.EnumerateObject().Any(p => expected.Contains(p.Name));
    }
}
