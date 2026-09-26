namespace ICOGenerator.Services.Llm;

/// <summary>
/// Small quirk table for the official OpenAI API, shared by the request-patching HTTP handler
/// (<see cref="LlmRequestCompatibilityHandler"/>) and the call-log request preview so both agree on the
/// body that is actually sent. Endpoints that are merely OpenAI-<i>compatible</i> (DeepSeek, local
/// servers, …) are more lenient and are left untouched.
/// </summary>
internal static class OpenAiCompatibility
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    /// <summary>True for the official OpenAI API host (and Azure-style <c>*.openai.com</c> gateways).</summary>
    public static bool IsOpenAiHost(string? host) =>
        host is not null
        && (host.Equals("openai.com", Ci) || host.EndsWith(".openai.com", Ci));

    /// <summary>
    /// True for OpenAI reasoning models — the o-series (<c>o1</c>, <c>o3</c>, <c>o4-mini</c>, …) and every
    /// <c>gpt-N</c> with N ≥ 5 (<c>gpt-5</c>, <c>gpt-5-nano</c>, <c>gpt-5.6-luna</c>, <c>gpt-6-luna</c>, …).
    /// These reject sampling parameters such as a <c>temperature</c> other than the default (1), returning
    /// HTTP 400 <c>unsupported_value</c>.
    /// <para>
    /// So theo SỐ phiên bản chứ không theo tiền tố <c>gpt-5</c>: khớp chuỗi cứng từng để lọt <c>gpt-6-luna</c>
    /// — model mới ra là mọi lượt BA chat 400 ngay, dù quy tắc của OpenAI vẫn y nguyên.
    /// </para>
    /// </summary>
    public static bool IsReasoningModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var id = modelId.Trim();
        if (id.StartsWith("gpt-", Ci))
        {
            var digits = id.AsSpan(4);
            var end = 0;
            while (end < digits.Length && char.IsDigit(digits[end]))
                end++;
            return end > 0 && int.TryParse(digits[..end], out var major) && major >= 5;
        }

        // o-series: a leading 'o' followed by a digit (o1 / o3 / o4-mini / …).
        return id.Length >= 2 && (id[0] is 'o' or 'O') && char.IsDigit(id[1]);
    }

    /// <summary>
    /// Giá trị <c>prompt_cache_retention</c> gửi kèm mọi lời gọi tới OpenAI thật. Mặc định của OpenAI chỉ
    /// giữ prefix cache 5–10 phút không hoạt động, mà một buổi phỏng vấn BA nghỉ lâu hơn thế thường xuyên
    /// (người dùng đọc lại tài liệu, đi họp, nghĩ một câu khó) — để mặc định là trượt cache đúng ở hội
    /// thoại dài, nơi prompt đắt nhất. Đặt rỗng để thôi gửi trường này.
    /// <para>
    /// Là hằng số chứ không phải cấu hình vì đây là quy ước phải GIỐNG NHAU ở hai nơi: handler dựng body
    /// thật và bản xem trước trong call log. Một trong hai đọc cấu hình còn nơi kia đọc mặc định là call
    /// log nói dối đúng vào lúc người ta mở nó ra để hỏi "lượt này có xin cache không".
    /// </para>
    /// </summary>
    public const string PromptCacheRetention = "24h";

    /// <summary>Host of an absolute endpoint URL, or <c>null</c> if it isn't a well-formed absolute URI.</summary>
    public static string? HostOf(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : null;
}
