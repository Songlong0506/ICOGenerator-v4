using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

public interface IModelCallLogger
{
    Task LogAsync(Guid projectId, Agent agent, LlmCallResult callResult, int step, string purpose, Guid? workflowRunId = null);
}
