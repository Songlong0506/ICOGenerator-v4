using System.Text;
using System.Text.Json.Nodes;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Patches outgoing chat-completion request bodies so a single set of prompts/agents works across the
/// different OpenAI-compatible APIs the app can be pointed at (DeepSeek, local servers, the official
/// OpenAI API). These named clients are only ever used for LLM calls, so patching every POST body is safe.
/// <list type="bullet">
///   <item><b>Non-OpenAI endpoints</b> (e.g. DeepSeek): re-adds the non-standard
///         <c>"thinking": { "type": "disabled" }</c> field to turn off reasoning output. The previous
///         hand-rolled client sent this directly; the typed OpenAI SDK has no property for it.</item>
///   <item><b>Official OpenAI API</b> (<c>*.openai.com</c>): the field above is omitted (OpenAI 400s on
///         unknown parameters). For reasoning models (o-series, gpt-5 family) the <c>temperature</c> field
///         is dropped as well — they only accept the default value and 400 on anything else.</item>
///   <item><b>Official OpenAI API</b>: prompt-cache fields are added — see <see cref="PatchPromptCache"/>.
///         Đây là chỗ tiết kiệm lớn nhất của cả app: prompt nền của BA chat
///         (<c>requirement-chat.v4.md</c>) là hơn 26.000 token ước lượng gửi lại NGUYÊN SI mỗi lượt, và
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
            if (TryPatch(body, request.RequestUri?.Host, out var patched))
                request.Content = new StringContent(patched, Encoding.UTF8, "application/json");
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static bool TryPatch(string body, string? host, out string patched)
    {
        patched = body;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            if (JsonNode.Parse(body) is not JsonObject obj)
                return false;

            var changed = OpenAiCompatibility.IsOpenAiHost(host)
                ? PatchOpenAi(obj)
                : PatchThinking(obj);

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

    // Official OpenAI API: never inject "thinking"; drop "temperature" for reasoning models that reject it;
    // add the prompt-cache fields.
    private static bool PatchOpenAi(JsonObject obj)
    {
        var modelId = obj["model"] is JsonValue v && v.TryGetValue(out string? id) ? id : null;
        var changed = OpenAiCompatibility.IsReasoningModel(modelId)
            && obj.ContainsKey("temperature")
            && obj.Remove("temperature");

        return PatchPromptCache(obj) || changed;
    }

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

    // OpenAI-compatible endpoints (DeepSeek, local): re-add the reasoning-off field the SDK can't express.
    private static bool PatchThinking(JsonObject obj)
    {
        if (obj.ContainsKey("thinking"))
            return false;

        obj["thinking"] = new JsonObject { ["type"] = "disabled" };
        return true;
    }
}
