using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Llm;

public class ModelCallLogger : IModelCallLogger
{
    private readonly AppDbContext _db;

    public ModelCallLogger(AppDbContext db)
    {
        _db = db;
    }

    public async Task LogAsync(Guid projectId, Agent agent, LlmCallResult callResult, int step, string purpose, Guid? workflowRunId = null)
    {
        _db.AgentModelCallLogs.Add(new AgentModelCallLog
        {
            ProjectId = projectId,
            AgentId = agent.Id,
            WorkflowRunId = workflowRunId,
            AgentName = agent.RoleKey.GetTitle(),
            ModelId = callResult.ModelId,
            RequestJson = callResult.RequestJson,
            ResponseText = callResult.ResponseText,
            ErrorMessage = callResult.ErrorMessage,
            PromptTokens = callResult.PromptTokens,
            CompletionTokens = callResult.CompletionTokens,
            TotalTokens = callResult.TotalTokens,
            DurationMs = callResult.DurationMs,
            HttpStatusCode = callResult.HttpStatusCode,
            IsSuccess = callResult.IsSuccess,
            Step = step,
            Purpose = purpose
        });

        await _db.SaveChangesAsync();
    }
}
