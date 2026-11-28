using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Mọi thứ liên quan tới việc ĐỌC JSON do model trả về, gom một chỗ (trước đây rải ở hai static class
/// riêng cộng với một hàm <c>ParseFallback</c> chép tay ở gần chục service).
/// <list type="bullet">
///   <item><see cref="Options"/> — model không đảm bảo đúng hoa/thường tên field nên luôn so khớp
///         không phân biệt hoa thường.</item>
///   <item><see cref="ExtractObject"/> — bóc object JSON ra khỏi hàng rào ```json / văn dẫn quanh nó.</item>
///   <item><see cref="TryDeserialize{T}"/> — bóc + đọc vào <c>T</c>, KHÔNG NÉM: mọi thứ
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
    /// <param name="options">
    /// Bộ tuỳ chọn đọc RIÊNG của caller — chỉ dùng khi <typeparamref name="T"/> cần một converter mà
    /// <see cref="Options"/> không có (bản đồ bao phủ: trường <c>known</c> đọc được cả dạng chuỗi cũ lẫn
    /// mảng mới). Bỏ trống ⇒ dùng <see cref="Options"/> như mọi parser khác. Truyền vào đây thay vì nhét
    /// converter ấy vào <see cref="Options"/> để nó không âm thầm đổi cách đọc MỌI <c>List&lt;string&gt;</c>
    /// mà model trả về ở các service khác.
    /// </param>
    public static T? TryDeserialize<T>(string? text, bool requireKnownProperty = false, JsonSerializerOptions? options = null) where T : class
    {
        var json = ExtractObject(text);
        if (json.Length > 0 && TryRead<T>(json, requireKnownProperty, options) is { } value)
            return value;

        // Object đầu không dùng được — xem model có tự viết lại câu trả lời ở phía sau không, lấy bản CUỐI
        // đọc được. Ca thật (JD Library, AI Design Spec V3, 2026-11-26): ~35k ký tự spec đổ thô sau thẻ
        // `<parameter name="content">` làm hỏng object đầu, rồi model viết lại TRỌN câu trả lời thành một
        // object JSON hợp lệ ở dòng mới. Chỉ đọc object đầu thì bỏ phí bản đúng và rơi vào khung dự phòng.
        T? last = null;
        foreach (var candidate in LaterObjects(text, json))
        {
            if (TryRead<T>(candidate, requireKnownProperty, options) is { } later)
                last = later;
        }
        return last;
    }

    // Đọc một object đã bóc, kèm hai lượt sửa có ranh giới hẹp. Không ném.
    private static T? TryRead<T>(string json, bool requireKnownProperty, JsonSerializerOptions? options) where T : class
    {
        try
        {
            return Read<T>(json, requireKnownProperty, options);
        }
        catch (JsonException)
        {
        }

        // Thẻ tham số tool lọt vào JSON — xem RepairLeakedParameterTags.
        if (RepairLeakedParameterTags(json) is { } untagged)
        {
            try
            {
                return Read<T>(untagged, requireKnownProperty, options);
            }
            catch (JsonException)
            {
            }
        }

        // JSON hỏng ở tầng DÃY THOÁT — model nhả `\u1E1y` (ba chữ số hex rồi tới chữ cái) giữa một
        // câu tiếng Việt. Sửa rồi đọc lại; sửa không xong thì vẫn null như trước.
        var repaired = RepairStringEscapes(json);
        if (ReferenceEquals(repaired, json))
            return null;

        try
        {
            return Read<T>(repaired, requireKnownProperty, options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Các object JSON đứng ĐẦU DÒNG phía sau object đầu tiên — ứng viên cho một câu trả lời model tự viết
    /// lại sau một bản hỏng. Chỉ xét <c>'{'</c> đầu dòng (bỏ khoảng trắng đầu dòng) để không bóc nhầm một
    /// object con nằm giữa nội dung.
    /// </summary>
    private static IEnumerable<string> LaterObjects(string? text, string first)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var firstStart = first.Length > 0 ? text.IndexOf(first, StringComparison.Ordinal) : -1;
        for (var i = text.IndexOf('{', 1); i > 0; i = text.IndexOf('{', i + 1))
        {
            if (i == firstStart || !StartsLine(text, i))
                continue;

            var candidate = ExtractObject(text[i..]);
            if (candidate.Length > 0)
                yield return candidate;
        }
    }

    private static bool StartsLine(string text, int index)
    {
        for (var j = index - 1; j >= 0; j--)
        {
            if (text[j] == '\n') return true;
            if (text[j] != ' ' && text[j] != '\t' && text[j] != '\r') return false;
        }
        return true;
    }

    private static readonly Regex LeakedParameterTag =
        new(@"^\s*<parameter name=""(?<name>[A-Za-z_]\w*)"">", RegexOptions.Compiled);

    /// <summary>
    /// Dựng lại object con khi model viết nó bằng CÚ PHÁP THẺ của lời gọi tool thay vì JSON: một chuỗi mở
    /// đầu bằng <c>&lt;parameter name="content"&gt;</c> đứng ở chỗ schema đòi object
    /// <c>{"content": …}</c>. Trả null khi không có gì để sửa.
    ///
    /// <para>
    /// Ca thật: <c>claude-sonnet-5-5</c> trả câu trả lời qua tool <c>structured_response</c>, schema có object
    /// lồng (<c>aiDesignSpec: {content}</c>, <c>productBrief: {content}</c>), và thỉnh thoảng trả
    /// <c>"aiDesignSpec": "\n&lt;parameter name=\"content\"&gt;# AI Design Spec…"</c>. JSON vẫn hợp lệ nên
    /// không ai thấy lỗi — chỉ có kiểu sai, deserialize ném, và caller rơi vào khung dự phòng với toàn bộ
    /// nội dung thật nằm ngay trong phản hồi (4 ca từ 2026-11-15 tới 2026-11-25: Product Brief ×2, AI Design
    /// Spec, vòng sửa spec). Sửa theo đúng một hình dạng — thẻ ở ĐẦU chuỗi — nên không đụng tới nội dung
    /// nào khác.
    /// </para>
    /// </summary>
    private static string? RepairLeakedParameterTags(string json)
    {
        if (json.IndexOf("<parameter name=", StringComparison.Ordinal) < 0)
            return null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        var repaired = false;
        Walk(root);
        return repaired ? root!.ToJsonString() : null;

        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(p => p.Key).ToList())
                    {
                        if (Untag(obj[key]) is { } fixedValue)
                        {
                            obj[key] = fixedValue;
                            repaired = true;
                        }
                        else
                        {
                            Walk(obj[key]);
                        }
                    }
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (Untag(array[i]) is { } fixedValue)
                        {
                            array[i] = fixedValue;
                            repaired = true;
                        }
                        else
                        {
                            Walk(array[i]);
                        }
                    }
                    break;
            }
        }

        static JsonObject? Untag(JsonNode? value)
        {
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text))
                return null;

            var match = LeakedParameterTag.Match(text);
            if (!match.Success)
                return null;

            var body = text[match.Length..].TrimEnd();
            if (body.EndsWith("</parameter>", StringComparison.Ordinal))
                body = body[..^"</parameter>".Length].TrimEnd();

            return new JsonObject { [match.Groups["name"].Value] = body.TrimStart('\r', '\n') };
        }
    }

    private static T? Read<T>(string json, bool requireKnownProperty, JsonSerializerOptions? options) where T : class
    {
        if (requireKnownProperty && !SharesAnyPropertyWith<T>(json))
            return null;

        return JsonSerializer.Deserialize<T>(json, options ?? Options);
    }

    /// <summary>
    /// Sửa các DÃY THOÁT hỏng bên trong chuỗi JSON, chỉ chạy khi lần đọc đầu đã ném. Trả về CHÍNH
    /// <paramref name="json"/> (so bằng tham chiếu) khi không sửa gì — caller dùng đó để biết "không có
    /// gì để thử lại" thay vì gọi deserialize thêm một lần vô ích.
    ///
    /// <para>
    /// Vì sao phải có: model viết tiếng Việt hay nhả JSON dạng ASCII toàn `\uXXXX`, và chỉ cần MỘT dãy
    /// rụng một chữ số hex là cả object không đọc được. Ca thật (dự án JD Libary, lượt 6): `\u1E1y` trong
    /// chữ "vậy" ⇒ <c>BAChatReplyParser</c> rơi về nhánh text thuần và **nguyên khối JSON** —
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
