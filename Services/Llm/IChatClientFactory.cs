using ICOGenerator.Domain;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Builds a Microsoft.Extensions.AI <see cref="IChatClient"/> for a given <see cref="AiModel"/>.
/// This is the seam introduced in step 1 of the MAF adoption: callers keep talking to
/// <see cref="ILlmClient"/>, but the actual wire protocol now goes through the standard
/// <see cref="IChatClient"/> abstraction so logging/tool-calling middleware can be layered on later.
/// </summary>
public interface IChatClientFactory
{
    IChatClient Create(AiModel model);
}
