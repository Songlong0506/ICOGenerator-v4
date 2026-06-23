using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Decides whether a given model uses MEAI structured output (the OpenAI <c>response_format: json_schema</c>
/// parameter) for the BA's JSON-returning calls, or the legacy "ask for JSON in the prompt then parse the
/// text" path. Config-bound and testable in isolation.
///
///   "Llm": { "StructuredOutput": { "Enabled": true, "ModelIds": [ "gpt-4o-mini" ] } }
///
/// This is an OPT-IN allowlist — structured output is only requested for the
/// listed model ids, and only when <c>Enabled</c> is true. The default (disabled, empty list) leaves every
/// call on the existing text + hand-written-parser path, because many weak / local OpenAI-compatible
/// servers reject the <c>response_format</c> parameter. Even when on, callers keep their parser as a
/// fallback, so a model that returns JSON not matching the schema still degrades gracefully.
/// </summary>
public sealed class StructuredOutputPolicy
{
    private readonly bool _enabled;
    private readonly HashSet<string> _modelIds;

    public StructuredOutputPolicy(IConfiguration configuration)
        : this(
            configuration.GetValue("Llm:StructuredOutput:Enabled", false),
            configuration.GetSection("Llm:StructuredOutput:ModelIds").Get<string[]>() ?? [])
    {
    }

    public StructuredOutputPolicy(bool enabled, IEnumerable<string> modelIds)
    {
        _enabled = enabled;
        _modelIds = new HashSet<string>(modelIds, StringComparer.OrdinalIgnoreCase);
    }

    public bool UseStructuredOutput(AiModel model) =>
        _enabled && _modelIds.Contains(model.ModelId);
}
