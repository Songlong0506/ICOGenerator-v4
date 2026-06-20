using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Logging;

public interface IModelCallLogger
{
    Task LogAsync(Guid projectId, Agent agent, LlmCallResult callResult, int step, string purpose, Guid? workflowRunId = null);
}
