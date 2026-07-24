using System.Text;
using System.Text.Json.Nodes;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Re-adds the non-standard <c>"thinking": { "type": "disabled" }</c> field to outgoing
/// chat-completion request bodies. The previous hand-rolled client sent this field directly to turn
/// off reasoning output on models that honour it (e.g. DeepSeek); the typed OpenAI SDK has no property
/// for it, so it is injected here in the HttpClient pipeline to keep behaviour identical. These named
/// clients are only ever used for LLM calls, so patching every POST body is safe.
/// <para>
/// The official OpenAI API rejects unknown parameters with HTTP 400
/// (<c>unknown_parameter: 'thinking'</c>), so the field is <b>not</b> injected when the request targets
/// an <c>openai.com</c> host. OpenAI models don't honour this field anyway.
/// </para>
/// </summary>
internal sealed class ThinkingDisabledHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null && request.Method == HttpMethod.Post && !TargetsOpenAI(request.RequestUri))
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            if (TryAddThinking(body, out var patched))
                request.Content = new StringContent(patched, Encoding.UTF8, "application/json");
        }

        return await base.SendAsync(request, cancellationToken);
    }

    // The official OpenAI API validates the request schema strictly and 400s on the non-standard
    // "thinking" field. Skip the injection for it (and any *.openai.com host, e.g. Azure gateways).
    private static bool TargetsOpenAI(Uri? uri)
    {
        var host = uri?.Host;
        return host is not null
            && (host.Equals("openai.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryAddThinking(string body, out string patched)
    {
        patched = body;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            if (JsonNode.Parse(body) is not JsonObject obj || obj.ContainsKey("thinking"))
                return false;

            obj["thinking"] = new JsonObject { ["type"] = "disabled" };
            patched = obj.ToJsonString();
            return true;
        }
        catch
        {
            return false; // not JSON we recognise — send it through unchanged
        }
    }
}
