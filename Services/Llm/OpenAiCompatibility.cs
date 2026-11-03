using System.Collections.Concurrent;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Small quirk table for the official OpenAI API, shared by the request-patching HTTP handler
/// (<see cref="LlmRequestCompatibilityHandler"/>) and the call-log request preview so both agree on the
/// body that is actually sent. Mọi trường KHÔNG chuẩn đều đi theo danh sách CHO PHÉP: chỉ gửi tới
/// endpoint đã biết là nhận nó (<see cref="AcceptsThinkingField"/>, <see cref="IsOpenAiHost"/>); endpoint
/// lạ — gateway công ty, Vertex, server local — nhận body nguyên dạng chuẩn.
/// </summary>
internal static class OpenAiCompatibility
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    /// <summary>True for the official OpenAI API host (and Azure-style <c>*.openai.com</c> gateways).</summary>
    public static bool IsOpenAiHost(string? host) =>
        host is not null
        && (host.Equals("openai.com", Ci) || host.EndsWith(".openai.com", Ci));

    /// <summary>
    /// True khi endpoint đã biết là NHẬN trường <c>"thinking": {"type":"disabled"}</c> — hiện chỉ DeepSeek API
    /// gốc (<c>api.deepseek.com</c>), nơi nó tắt chế độ suy luận của V4 (nhanh hơn, rẻ hơn).
    /// <para>
    /// Danh sách CHO PHÉP chứ không phải danh sách chặn: trường này không chuẩn, và Azure OpenAI, Bosch Model
    /// Farm, Vertex AI… trả 400 <c>unknown_parameter</c> với nó. Đoán sai một endpoint lạ theo hướng này chỉ
    /// tốn thêm ít token suy luận; theo hướng ngược lại là mọi lời gọi hỏng. vLLM/LM Studio bỏ qua trường lạ
    /// nên không cần có mặt ở đây. Thêm nhà cung cấp mới nhận trường này = thêm host vào đây.
    /// </para>
    /// </summary>
    public static bool AcceptsThinkingField(string? host) =>
        host is not null
        && (host.Equals("deepseek.com", Ci) || host.EndsWith(".deepseek.com", Ci));

    /// <summary>
    /// True cho endpoint kiểu Azure OpenAI: host <c>*.openai.azure.com</c>, hoặc bất kỳ gateway nào dùng
    /// đường dẫn <c>/openai/deployments/{deployment}</c> (vd. Bosch Model Farm
    /// <c>https://aoai-farm.bosch-temp.com/api/openai/deployments/gpt-6-luna-2026-09-22?api-version=…</c>).
    /// Loại này 400 với MỌI tham số lạ (<c>"Unknown parameter: 'thinking'"</c>) nên body phải đi nguyên
    /// dạng chuẩn — kể cả khi host trùng tên miền được cho phép ở trên (vd. gateway Azure trên
    /// <c>*.openai.com</c>): không <c>thinking</c>, không hai trường prompt cache.
    /// Nhận theo đường dẫn chứ không theo host vì gateway nội bộ mỗi nơi một tên miền.
    /// </summary>
    public static bool IsAzureOpenAiStyle(Uri? uri) =>
        uri is not null
        && (uri.Host.EndsWith(".openai.azure.com", Ci)
            || uri.AbsolutePath.Contains("/openai/deployments/", Ci));

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

    /// <summary>
    /// Các cặp (host, model) đã trả 400 vì <c>reasoning_effort</c> khi request có function tools — vd.
    /// <c>gpt-6-luna-2026-09-22</c> trên <c>/v1/chat/completions</c> (Bosch Model Farm):
    /// <c>"Function tools with reasoning_effort are not supported … set reasoning_effort to 'none'"</c>. Model
    /// reasoning loại này mặc định suy luận, nên KHÔNG gửi gì cũng bị coi là có reasoning_effort.
    /// <para>
    /// Học theo phản ứng chứ không chèn sẵn: model không phải reasoning (gpt-4o, server local…) lại 400
    /// <c>unsupported_parameter</c> nếu thấy trường này, nên chỉ gửi <c>"none"</c> cho cặp đã TỪNG từ chối.
    /// <see cref="LlmRequestCompatibilityHandler"/> ghi vào đây ở lần 400 đầu tiên rồi gửi lại ngay; mọi lời
    /// gọi sau đi thẳng với <c>"none"</c>, khỏi tốn round-trip hỏng ở từng bước của agent. Bộ nhớ trong
    /// tiến trình là đủ: khởi động lại chỉ tốn đúng một lần 400 nữa.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> ReasoningOffWithTools = new(StringComparer.OrdinalIgnoreCase);

    private static string ReasoningKey(string? host, string? modelId) => $"{host}|{modelId}";

    public static bool RequiresReasoningOffWithTools(string? host, string? modelId) =>
        host is not null && modelId is not null && ReasoningOffWithTools.ContainsKey(ReasoningKey(host, modelId));

    public static void RememberReasoningOffWithTools(string host, string modelId) =>
        ReasoningOffWithTools[ReasoningKey(host, modelId)] = 0;

    /// <summary>Giá trị <c>reasoning_effort</c> gửi cho cặp (host, model) đã học ở trên.</summary>
    public const string ReasoningEffortOff = "none";

    /// <summary>Host of an absolute endpoint URL, or <c>null</c> if it isn't a well-formed absolute URI.</summary>
    public static string? HostOf(string? endpoint) => UriOf(endpoint)?.Host;

    /// <summary>The endpoint as an absolute URI, or <c>null</c> if it isn't a well-formed absolute URI.</summary>
    public static Uri? UriOf(string? endpoint) =>
        Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) ? uri : null;
}
