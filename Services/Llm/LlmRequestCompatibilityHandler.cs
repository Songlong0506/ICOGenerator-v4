using System.Text;
using System.Text.Json.Nodes;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Patches outgoing chat-completion request bodies so a single set of prompts/agents works across the
/// different OpenAI-compatible APIs the app can be pointed at (DeepSeek, local servers, the official
/// OpenAI API). These named clients are only ever used for LLM calls, so patching every POST body is safe.
/// <list type="bullet">
///   <item><b>DeepSeek API</b> (<see cref="OpenAiCompatibility.AcceptsThinkingField"/>): re-adds the
///         non-standard <c>"thinking": { "type": "disabled" }</c> field to turn off reasoning output. The
///         typed OpenAI SDK has no property for it.</item>
///   <item><b>Mọi endpoint khác</b> (OpenAI, Vertex AI, gateway công ty, server local): không chèn
///         <c>thinking</c> — trường lạ là 400 <c>unknown_parameter</c> ở các API nghiêm ngặt.</item>
///   <item><b>Mọi endpoint OpenAI-compatible KHÔNG phải OpenAI/Azure</b>: đổi trần output từ
///         <c>max_completion_tokens</c> (tên SDK OpenAI 2.x gửi) về <c>max_tokens</c> — xem
///         <see cref="PatchMaxTokens"/>.</item>
///   <item><b>Azure-style endpoints</b> (<c>*.openai.azure.com</c>, <c>/openai/deployments/…</c> — vd.
///         Bosch Model Farm): body đi nguyên, không thêm gì (400 <c>unknown_parameter</c>).</item>
///   <item><b>Official OpenAI API</b>: prompt-cache fields are added — see <see cref="PatchPromptCache"/>.
///         Đây là chỗ tiết kiệm lớn nhất của cả app: prompt nền của BA chat
///         (<c>requirement-chat.v4.md</c>) là khoảng 19.000 token gửi lại NGUYÊN SI mỗi lượt, và
///         token đọc từ cache rẻ hơn token input đầy đủ 10 lần.</item>
///   <item><b>Mọi endpoint</b>: request có tool bị 400 vì <c>reasoning_effort</c> ⇒ gửi lại một lần với
///         <c>reasoning_effort: "none"</c> và nhớ cho các lượt sau — xem <see cref="RejectsReasoningWithTools"/>.</item>
/// </list>
/// </summary>
internal sealed class LlmRequestCompatibilityHandler : DelegatingHandler
{
    // Các (endpoint, model) đã từng 400 vì "function tools với reasoning_effort" — từ đó gửi sẵn
    // reasoning_effort=none khi request có tool, khỏi đốt một lượt 400 ở MỖI bước agent. Sống theo process:
    // restart thì học lại bằng đúng một lượt 400.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ReasoningOffWithTools = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null && request.Method == HttpMethod.Post)
        {
            var original = await request.Content.ReadAsStringAsync(cancellationToken);
            body = original;
            if (TryPatch(body, request.RequestUri, out var patched))
                body = patched;
            if (!ReasoningOffWithTools.IsEmpty
                && ReasoningOffWithTools.ContainsKey(ModelKey(request.RequestUri, body))
                && TryDisableReasoning(body, out var reasoningOff))
                body = reasoningOff;
            if (!ReferenceEquals(body, original))
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (body is null || response.StatusCode != System.Net.HttpStatusCode.BadRequest || !TryDisableReasoning(body, out var retryBody))
            return response;

        // Đọc thân lỗi vào buffer để caller vẫn đọc lại được nếu không thử lại.
        await response.Content.LoadIntoBufferAsync();
        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!RejectsReasoningWithTools(error))
            return response;

        ReasoningOffWithTools[ModelKey(request.RequestUri, body)] = true;
        response.Dispose();
        return await base.SendAsync(CloneWithBody(request, retryBody), cancellationToken);
    }

    /// <summary>
    /// Model reasoning của OpenAI (vd <c>gpt-6-luna</c>) trên <c>/v1/chat/completions</c> từ chối MỌI request có
    /// tool khi mức suy luận khác <c>none</c> — kể cả khi app không gửi <c>reasoning_effort</c>, vì mặc định của
    /// model không phải <c>none</c>: <i>"Function tools with reasoning_effort are not supported for gpt-6-luna in
    /// /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'."</i>.
    /// Agent loop thì lượt nào cũng có tool, nên model đó không chạy được bước nào. Cách duy nhất không phải
    /// viết lại cả đường gọi sang Responses API: gửi lại với <c>reasoning_effort: "none"</c> — model mất phần suy
    /// luận ẩn ở những lượt có tool, đổi lại chạy được. Nhận theo CHÍNH thông điệp lỗi chứ không theo tên model.
    /// </summary>
    internal static bool RejectsReasoningWithTools(string? error) =>
        error is not null
        && error.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase)
        && error.Contains("tools", StringComparison.OrdinalIgnoreCase);

    // Chỉ khi request CÓ tool và chưa tự đặt reasoning_effort=none.
    private static bool TryDisableReasoning(string body, out string patched)
    {
        patched = body;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject obj
                || obj["tools"] is not JsonArray { Count: > 0 }
                || obj["reasoning_effort"]?.GetValue<string>() == "none")
                return false;

            obj["reasoning_effort"] = "none";
            patched = obj.ToJsonString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ModelKey(Uri? uri, string body)
    {
        string? model = null;
        try { model = (JsonNode.Parse(body) as JsonObject)?["model"]?.GetValue<string>(); } catch { }
        return $"{uri?.GetLeftPart(UriPartial.Path)}|{model}";
    }

    private static HttpRequestMessage CloneWithBody(HttpRequestMessage original, string body)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in original.Options)
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        return clone;
    }

    private static bool TryPatch(string body, Uri? uri, out string patched)
    {
        patched = body;
        // Azure OpenAI (và gateway kiểu Azure như Bosch Model Farm) 400 với mọi tham số lạ — gửi nguyên.
        if (string.IsNullOrWhiteSpace(body) || OpenAiCompatibility.IsAzureOpenAiStyle(uri))
            return false;

        try
        {
            if (JsonNode.Parse(body) is not JsonObject obj)
                return false;

            var host = uri?.Host;
            var changed = OpenAiCompatibility.IsOpenAiHost(host) ? PatchOpenAi(obj)
                : OpenAiCompatibility.AcceptsThinkingField(host) && PatchThinking(obj);
            if (OpenAiCompatibility.UsesLegacyMaxTokens(uri))
                changed |= PatchMaxTokens(obj);

            if (!changed)
                return false;

            patched = obj.ToJsonString();
            return true;
        }
        catch
        {
            return false; // not JSON we recognise — send it through unchanged
        }
    }

    // Official OpenAI API: never inject "thinking"; add the prompt-cache fields.
    private static bool PatchOpenAi(JsonObject obj) => PatchPromptCache(obj);

    /// <summary>
    /// Hai trường điều khiển prompt cache của OpenAI. Cache tự bật cho mọi prompt từ 1024 token trở lên
    /// và khớp theo PREFIX, nên không có gì phải khai báo phần nào được cache — nhưng hai thứ này thì
    /// phải tự đặt:
    /// <list type="bullet">
    ///   <item><c>prompt_cache_retention</c>: kéo TTL từ 5–10 phút lên 24 giờ (xem
    ///         <see cref="OpenAiCompatibility.PromptCacheRetention"/>).</item>
    ///   <item><c>prompt_cache_key</c>: gợi ý định tuyến, đặt theo project để các lượt của cùng một dự án
    ///         (cùng prompt nền + cùng tài liệu nguồn) về cùng backend. Xem <see cref="LlmCacheScope"/>.</item>
    /// </list>
    /// Không ghi đè nếu caller đã tự đặt.
    /// </summary>
    private static bool PatchPromptCache(JsonObject obj)
    {
        var changed = false;

        if (OpenAiCompatibility.PromptCacheRetention.Length > 0 && !obj.ContainsKey("prompt_cache_retention"))
        {
            obj["prompt_cache_retention"] = OpenAiCompatibility.PromptCacheRetention;
            changed = true;
        }

        if (LlmCacheScope.CacheKey is { Length: > 0 } key && !obj.ContainsKey("prompt_cache_key"))
        {
            obj["prompt_cache_key"] = key;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// SDK OpenAI 2.x gửi trần output dưới tên <c>max_completion_tokens</c> — tên chỉ OpenAI/Azure hiểu.
    /// DeepSeek (API gốc lẫn qua gateway) chỉ đọc <c>max_tokens</c> và LẶNG LẼ bỏ qua trường kia, nên trần
    /// app tự tính (vd. 60k mỗi đoạn của <c>PocOneShotBuilder</c>) không bao giờ tới nơi: model dừng ở trần
    /// mặc định của server (ca thật: mọi lượt <c>deepseek-v4-flash</c> cụt đúng 8.192 token với
    /// <c>finish_reason=length</c>, bắt phải gọi viết tiếp gấp đôi số lần). <c>max_tokens</c> là tên mọi server
    /// OpenAI-compatible đều nhận (DeepSeek, vLLM, Ollama, LM Studio, Vertex, LiteLLM) nên ĐỔI TÊN chứ không
    /// gửi cả hai — vài server 400 khi có đồng thời hai trường. Caller đã tự đặt <c>max_tokens</c> thì giữ.
    /// </summary>
    private static bool PatchMaxTokens(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("max_completion_tokens", out var value))
            return false;

        obj.Remove("max_completion_tokens");
        if (!obj.ContainsKey("max_tokens"))
            obj["max_tokens"] = value;
        return true;
    }

    // DeepSeek API: re-add the reasoning-off field the SDK can't express.
    private static bool PatchThinking(JsonObject obj)
    {
        if (obj.ContainsKey("thinking"))
            return false;

        obj["thinking"] = new JsonObject { ["type"] = "disabled" };
        return true;
    }
}
