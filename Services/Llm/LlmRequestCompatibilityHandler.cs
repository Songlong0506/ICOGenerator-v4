using System.Collections.Concurrent;
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
///   <item><b>Model reasoning không nhận tool trên chat/completions</b> (mọi endpoint, kể cả Azure-style): xem
///         <see cref="NeedsReasoningOff"/>.</item>
/// </list>
/// </summary>
internal sealed class LlmRequestCompatibilityHandler : DelegatingHandler
{
    /// <summary>
    /// Endpoint + model đã từng trả 400 "tools với reasoning_effort không được hỗ trợ". Nhớ lại để các lượt sau
    /// gửi <c>reasoning_effort: "none"</c> ngay từ đầu thay vì tốn thêm một round-trip bị từ chối mỗi bước agent.
    /// Sống theo process — khởi động lại thì học lại sau đúng một lần 400.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> ReasoningOffTargets = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null || request.Method != HttpMethod.Post)
            return await base.SendAsync(request, cancellationToken);

        var body = await request.Content.ReadAsStringAsync(cancellationToken);
        if (TryPatch(body, request.RequestUri, out var patched))
            body = patched;

        var target = ReasoningTarget(body, request.RequestUri);
        if (target is not null && ReasoningOffTargets.ContainsKey(target) && WithReasoningOff(body) is { } off)
            body = off;

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await base.SendAsync(request, cancellationToken);

        if (target is null || response.StatusCode != HttpStatusCode.BadRequest || WithReasoningOff(body) is not { } retryBody)
            return response;

        await response.Content.LoadIntoBufferAsync();
        if (!NeedsReasoningOff(await response.Content.ReadAsStringAsync(cancellationToken)))
            return response;

        ReasoningOffTargets[target] = true;
        response.Dispose();
        var retry = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            Content = new StringContent(retryBody, Encoding.UTF8, "application/json"),
        };
        foreach (var header in request.Headers)
            retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options)
            ((IDictionary<string, object?>)retry.Options)[option.Key] = option.Value;
        return await base.SendAsync(retry, cancellationToken);
    }

    /// <summary>
    /// Model reasoning của OpenAI (vd <c>gpt-6-luna-2026-09-22</c>) mặc định BẬT suy luận, và trên
    /// <c>/v1/chat/completions</c> thì từ chối tool khi đang suy luận: <c>400 invalid_request_error, param
    /// reasoning_effort: "Function tools with reasoning_effort are not supported … set reasoning_effort to
    /// 'none'"</c>. App không hề gửi <c>reasoning_effort</c> — đó là mặc định của model — nên mọi lượt agent
    /// (lượt nào cũng khai tool) chết ngay bước đầu, trong khi chat BA (không tool) vẫn chạy.
    /// <para>
    /// Không gửi <c>"none"</c> sẵn cho mọi request có tool: model không reasoning (gpt-4o, server local…) 400 với
    /// tham số lạ. Nên làm đúng điều lỗi bảo, và chỉ sau khi CHÍNH endpoint đó nói ra: khớp tên tham số
    /// <c>reasoning_effort</c> cùng giá trị <c>none</c> trong body lỗi.
    /// </para>
    /// </summary>
    internal static bool NeedsReasoningOff(string errorBody) =>
        errorBody.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase)
        && errorBody.Contains("'none'", StringComparison.OrdinalIgnoreCase);

    /// <summary>Khóa nhớ: URL (không query) + model trong body. Null nếu request không khai tool.</summary>
    private static string? ReasoningTarget(string body, Uri? uri)
    {
        if (uri is null)
            return null;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject obj || obj["tools"] is not JsonArray { Count: > 0 })
                return null;
            return $"{uri.GetLeftPart(UriPartial.Path)}|{obj["model"]?.ToString()}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Body đã đặt <c>reasoning_effort: "none"</c>; null nếu caller đã tự đặt (không ghi đè lựa chọn của nó).</summary>
    private static string? WithReasoningOff(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject obj || obj.ContainsKey("reasoning_effort"))
                return null;
            obj["reasoning_effort"] = "none";
            return obj.ToJsonString();
        }
        catch
        {
            return null;
        }
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
