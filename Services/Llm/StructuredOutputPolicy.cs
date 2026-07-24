using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Decides whether a given model uses MEAI structured output (the OpenAI <c>response_format: json_schema</c>
/// parameter) for the BA's JSON-returning calls, or the legacy "ask for JSON in the prompt then parse the
/// text" path.
///
/// This is a per-model, OPT-IN flag: structured output is only requested when the model's
/// <see cref="AiModel.SupportsStructuredOutput"/> is ticked on the Models admin screen. The default (false)
/// leaves every call on the existing text + hand-written-parser path, because many weak / local
/// OpenAI-compatible servers reject the <c>response_format</c> parameter. Even when on, callers keep their
/// parser as a fallback, so a model that returns JSON not matching the schema still degrades gracefully.
/// </summary>
public sealed class StructuredOutputPolicy
{
    public bool UseStructuredOutput(AiModel model) =>
        model.SupportsStructuredOutput;
}
