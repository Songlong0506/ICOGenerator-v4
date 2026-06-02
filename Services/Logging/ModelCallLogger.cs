using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Agents;

namespace ICOGenerator.Services.Logging;

public class ModelCallLogger : IModelCallLogger
{
    private readonly AppDbContext _db;

    public ModelCallLogger(AppDbContext db)
    {
        _db = db;
    }

    public async Task LogAsync(Guid projectId, Agent agent, LocalLlmCallResult callResult, int step, string purpose)
    {
        _db.AgentModelCallLogs.Add(new AgentModelCallLog
        {
            ProjectId = projectId,
            AgentId = agent.Id,
            AgentName = agent.Name,
            ModelName = callResult.ModelName,
            ModelId = callResult.ModelId,
            Endpoint = callResult.Endpoint,
            RequestJson = callResult.RequestJson,
            ResponseText = callResult.ResponseText,
            ExtractedContent = callResult.ExtractedContent,
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
