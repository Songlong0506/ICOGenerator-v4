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

    // Official OpenAI API: never inject "thinking"; drop "temperature" for reasoning models that reject it.
    private static bool PatchOpenAi(JsonObject obj)
    {
        var modelId = obj["model"] is JsonValue v && v.TryGetValue(out string? id) ? id : null;
        if (OpenAiCompatibility.IsReasoningModel(modelId) && obj.ContainsKey("temperature"))
            return obj.Remove("temperature");

        return false;
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
