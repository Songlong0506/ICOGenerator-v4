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
/// </list>
/// </summary>
internal sealed class LlmRequestCompatibilityHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null && request.Method == HttpMethod.Post)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            if (TryPatch(body, request.RequestUri, out var patched))
                request.Content = new StringContent(patched, Encoding.UTF8, "application/json");
        }

        return await base.SendAsync(request, cancellationToken);
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
