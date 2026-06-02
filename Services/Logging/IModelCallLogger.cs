using ICOGenerator.Domain;
using ICOGenerator.Services.Agents;

namespace ICOGenerator.Services.Logging;

public interface IModelCallLogger
{
    Task LogAsync(Guid projectId, Agent agent, LocalLlmCallResult callResult, int step, string purpose);
}
