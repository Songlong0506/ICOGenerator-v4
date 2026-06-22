using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Decides whether a given model uses the native function-calling path (the default) or the
/// prompt-based JSON-action fallback. Driven by configuration so a deployment can keep weak / local
/// models that don't support the OpenAI "tools" parameter on the legacy fallback:
///
///   "Llm": { "NativeToolCalling": { "Enabled": true, "FallbackModelIds": [ "some-model-id" ] } }
///
/// Enabled=false forces every model onto the fallback (a global kill-switch); otherwise a model whose
/// <see cref="AiModel.ModelId"/> is listed in FallbackModelIds uses the fallback and all others use
/// native tools. Kept as a tiny, config-bound service so the choice is testable in isolation.
/// </summary>
public sealed class NativeToolCallingPolicy
{
    private readonly bool _enabled;
    private readonly HashSet<string> _fallbackModelIds;

    public NativeToolCallingPolicy(IConfiguration configuration)
        : this(
            configuration.GetValue("Llm:NativeToolCalling:Enabled", true),
            configuration.GetSection("Llm:NativeToolCalling:FallbackModelIds").Get<string[]>() ?? [])
    {
    }

    public NativeToolCallingPolicy(bool enabled, IEnumerable<string> fallbackModelIds)
    {
        _enabled = enabled;
        _fallbackModelIds = new HashSet<string>(fallbackModelIds, StringComparer.OrdinalIgnoreCase);
    }

    public bool UseNativeTools(AiModel model) =>
        _enabled && !_fallbackModelIds.Contains(model.ModelId);
}
