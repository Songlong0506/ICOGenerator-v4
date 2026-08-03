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
            if (requireKnownProperty && !SharesAnyPropertyWith<T>(json))
                return null;

            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
