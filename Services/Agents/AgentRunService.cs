using System.Text.Json;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Models;
using ICOGenerator.Services.Registry;
using ICOGenerator.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Agents;

public class AgentRunService
{
    private readonly AppDbContext _db;
    private readonly IToolRegistry _toolRegistry;
    private readonly DynamicToolInvoker _invoker;
    private readonly LocalLlmClient _llm;
    private readonly AgentPromptBuilder _promptBuilder;
    private readonly WorkspaceTools _workspaceTools;

    public AgentRunService(AppDbContext db, IToolRegistry toolRegistry, DynamicToolInvoker invoker, LocalLlmClient llm, AgentPromptBuilder promptBuilder, WorkspaceTools workspaceTools)
    { _db = db; _toolRegistry = toolRegistry; _invoker = invoker; _llm = llm; _promptBuilder = promptBuilder; _workspaceTools = workspaceTools; }

    public async Task<string> RunAsync(Guid projectId, Guid agentId, string userMessage, int maxSteps = 2)
    {
        var project = await _db.Projects.FindAsync(projectId) ?? throw new InvalidOperationException("Project not found.");
        var agent = await _db.Agents.Include(x => x.AiModel).FirstAsync(x => x.Id == agentId);
        if (agent.AiModel == null) throw new InvalidOperationException("Agent model is not configured.");
        _workspaceTools.SetWorkspace(project.Name);
        var tools = await _toolRegistry.GetToolsForAgentAsync(agentId);
        var messages = new List<ChatMessageDto>
        {
            new() { Role = "system", Content = _promptBuilder.Build(agent, tools) },
            new() { Role = "user", Content = userMessage }
        };

        for (var step = 1; step <= maxSteps; step++)
        {
            var callResult = await _llm.ChatWithLogAsync(agent.AiModel, messages, agent.Temperature);
            await SaveModelCallLog(projectId, agent, callResult, step, "AgentRun");
            var response = callResult.Content;
            AgentActionDto? action;
            try
            {
                action = JsonSerializer.Deserialize<AgentActionDto>(JsonExtractor.Extract(response), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch(Exception ex)
            {
                return response;
            }
            if (action == null) return response;
            if (action.Type.Equals("final", StringComparison.OrdinalIgnoreCase))
            {
                await SaveConversation(projectId, agentId, action.Content ?? response);
                return action.Content ?? response;
            }
            if (action.Type.Equals("tool", StringComparison.OrdinalIgnoreCase))
            {
                var tool = tools.FirstOrDefault(x =>
                    x.Definition.Name.Equals(action.Tool, StringComparison.OrdinalIgnoreCase));

                var observation = tool == null
                    ? $"Tool not found: {action.Tool}"
                    : await _invoker.InvokeAsync(tool, action.Args);

                // Stop immediately for POC Developer flow
                if (agent.Name.Equals("Developer", StringComparison.OrdinalIgnoreCase)
                    && action.Tool?.Equals("WriteFile", StringComparison.OrdinalIgnoreCase) == true
                    && observation.Contains("poc-demo.html", StringComparison.OrdinalIgnoreCase)
                    && observation.Contains("File written", StringComparison.OrdinalIgnoreCase))
                {
                    var finalMessage = "POC demo created successfully: poc-demo.html";

                    await SaveConversation(projectId, agentId, finalMessage);

                    return finalMessage;
                }

                messages.Add(new() { Role = "assistant", Content = response });
                messages.Add(new() { Role = "user", Content = "OBSERVATION:\n" + observation });

                if (observation.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
                    && observation.Contains("0 Error", StringComparison.OrdinalIgnoreCase))
                {
                    await SaveConversation(projectId, agentId, "Build succeeded.");
                    return "Build succeeded.";
                }
            }
            //if (action.Type.Equals("tool", StringComparison.OrdinalIgnoreCase))
            //{
            //    var tool = tools.FirstOrDefault(x => x.Definition.Name.Equals(action.Tool, StringComparison.OrdinalIgnoreCase));
            //    var observation = tool == null ? $"Tool not found: {action.Tool}" : await _invoker.InvokeAsync(tool, action.Args);
            //    messages.Add(new() { Role = "assistant", Content = response });
            //    messages.Add(new() { Role = "user", Content = "OBSERVATION:\n" + observation });
            //    if (observation.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase) && observation.Contains("0 Error", StringComparison.OrdinalIgnoreCase))
            //    {
            //        await SaveConversation(projectId, agentId, "Build succeeded.");
            //        return "Build succeeded.";
            //    }
            //}
        }
        return "Stopped because max steps reached.";
    }

    private async Task SaveModelCallLog(Guid projectId, Agent agent, LocalLlmCallResult callResult, int step, string purpose)
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

    private async Task SaveConversation(Guid projectId, Guid agentId, string message, string role = "assistant")
    {
        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = agentId,
            Role = role,
            Message = message,
            TokenUsed = Math.Max(1, message.Length / 4)
        });

        await _db.SaveChangesAsync();
    }
}
