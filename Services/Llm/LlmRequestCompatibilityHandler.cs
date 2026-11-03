using System.Net;
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
///   <item><b>Azure-style endpoints</b> (<c>*.openai.azure.com</c>, <c>/openai/deployments/…</c> — vd.
///         Bosch Model Farm): body đi nguyên, không thêm gì (400 <c>unknown_parameter</c>).</item>
///   <item><b>Official OpenAI API</b>: prompt-cache fields are added — see <see cref="PatchPromptCache"/>.
///         Đây là chỗ tiết kiệm lớn nhất của cả app: prompt nền của BA chat
///         (<c>requirement-chat.v4.md</c>) là khoảng 19.000 token gửi lại NGUYÊN SI mỗi lượt, và
///         token đọc từ cache rẻ hơn token input đầy đủ 10 lần.</item>
///   <item><b>Model reasoning từ chối function tools</b> (mọi endpoint, kể cả kiểu Azure): lượt có
///         <c>tools</c> bị 400 nêu <c>reasoning_effort</c> thì gửi lại NGAY với
///         <c>"reasoning_effort": "none"</c> và nhớ cặp (host, model) đó — xem
///         <see cref="OpenAiCompatibility.RequiresReasoningOffWithTools"/>. Làm ở tầng HTTP để mọi đường gọi
///         (agent, BA chat, kiểm tra kết nối) cùng được cứu mà không ai phải tự xử lý.</item>
/// </list>
/// </summary>
internal sealed class LlmRequestCompatibilityHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null || request.Method != HttpMethod.Post)
            return await base.SendAsync(request, cancellationToken);

        var body = await request.Content.ReadAsStringAsync(cancellationToken);
        var mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";
        if (TryPatch(body, request.RequestUri, out var patched))
        {
            body = patched;
            request.Content = new StringContent(body, Encoding.UTF8, mediaType);
        }

        var response = await base.SendAsync(request, cancellationToken);

        // Model reasoning từ chối function tools khi đang suy luận: học cặp (host, model) rồi gửi lại với
        // reasoning_effort = "none". Chỉ một lần — lần gửi lại mà vẫn 400 thì trả nguyên lỗi đó lên.
        if (response.StatusCode != HttpStatusCode.BadRequest
            || request.RequestUri is not { } uri
            || !TryReasoningOffRetryBody(body, out var modelId, out var retryBody))
            return response;

        await response.Content.LoadIntoBufferAsync();
        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!RejectedReasoningWithTools(error))
            return response; // buffered nên tầng trên vẫn đọc lại được body lỗi

        OpenAiCompatibility.RememberReasoningOffWithTools(uri.Host, modelId);
        response.Dispose();
        request.Content = new StringContent(retryBody, Encoding.UTF8, mediaType);
        return await base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Thân lỗi của ca "function tools + reasoning_effort": nêu tên tham số <c>reasoning_effort</c> kèm chữ
    /// tool/function. Khớp theo tên tham số như <see cref="EndpointQuirks"/>, không theo từng nhà cung cấp.
    /// </summary>
    internal static bool RejectedReasoningWithTools(string? error) =>
        error is not null
        && error.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase)
        && (error.Contains("tool", StringComparison.OrdinalIgnoreCase)
            || error.Contains("function", StringComparison.OrdinalIgnoreCase));

    // Body đáng gửi lại: có tools, có model, và chưa phải đã xin "none" (gửi lại y nguyên là vô ích).
    private static bool TryReasoningOffRetryBody(string body, out string modelId, out string retryBody)
    {
        modelId = retryBody = string.Empty;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject obj
                || !HasTools(obj)
                || obj["model"] is not JsonValue modelNode
                || !modelNode.TryGetValue<string>(out var model)
                || string.IsNullOrWhiteSpace(model)
                || IsReasoningOff(obj))
                return false;

            obj["reasoning_effort"] = OpenAiCompatibility.ReasoningEffortOff;
            modelId = model;
            retryBody = obj.ToJsonString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasTools(JsonObject obj) => obj["tools"] is JsonArray { Count: > 0 };

    private static bool IsReasoningOff(JsonObject obj) =>
        obj["reasoning_effort"] is JsonValue v && v.TryGetValue<string>(out var effort)
        && string.Equals(effort, OpenAiCompatibility.ReasoningEffortOff, StringComparison.OrdinalIgnoreCase);

    // Cặp (host, model) đã học: gửi "none" ngay từ đầu cho lượt có tools mà caller chưa tự đặt mức nào.
    private static bool PatchLearnedReasoningOff(JsonObject obj, string? host)
    {
        if (!HasTools(obj) || obj.ContainsKey("reasoning_effort")
            || obj["model"] is not JsonValue m || !m.TryGetValue<string>(out var model)
            || !OpenAiCompatibility.RequiresReasoningOffWithTools(host, model))
            return false;

        obj["reasoning_effort"] = OpenAiCompatibility.ReasoningEffortOff;
        return true;
    }

    private static bool TryPatch(string body, Uri? uri, out string patched)
    {
        patched = body;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            if (JsonNode.Parse(body) is not JsonObject obj)
                return false;

            var host = uri?.Host;
            // reasoning_effort là tham số CHUẨN nên đi cả với endpoint kiểu Azure — chính ca gpt-6-luna trên
            // Bosch Model Farm cần nó.
            var changed = PatchLearnedReasoningOff(obj, host);

            // Azure OpenAI (và gateway kiểu Azure như Bosch Model Farm) 400 với mọi tham số lạ — không thêm
            // gì nữa.
            if (!OpenAiCompatibility.IsAzureOpenAiStyle(uri))
            {
                changed |= OpenAiCompatibility.IsOpenAiHost(host) ? PatchOpenAi(obj)
                    : OpenAiCompatibility.AcceptsThinkingField(host) && PatchThinking(obj);
            }

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

    // DeepSeek API: re-add the reasoning-off field the SDK can't express.
    private static bool PatchThinking(JsonObject obj)
    {
        if (obj.ContainsKey("thinking"))
            return false;

        obj["thinking"] = new JsonObject { ["type"] = "disabled" };
        return true;
    }
}
