using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Registry;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Logging;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Services.Agents;

public class AgentRunService
{
    private readonly AppDbContext _db;
    private readonly IToolRegistry _toolRegistry;
    private readonly DynamicToolInvoker _invoker;
    private readonly ILlmClient _llm;
    private readonly AgentPromptBuilder _promptBuilder;
    private readonly AgentActionParser _actionParser;
    private readonly WorkspaceTools _workspaceTools;
    private readonly IModelCallLogger _modelCallLogger;

    public AgentRunService(AppDbContext db, IToolRegistry toolRegistry, DynamicToolInvoker invoker, ILlmClient llm, AgentPromptBuilder promptBuilder, AgentActionParser actionParser, WorkspaceTools workspaceTools, IModelCallLogger modelCallLogger)
    { _db = db; _toolRegistry = toolRegistry; _invoker = invoker; _llm = llm; _promptBuilder = promptBuilder; _actionParser = actionParser; _workspaceTools = workspaceTools; _modelCallLogger = modelCallLogger; }

    public async Task<string> RunAsync(Guid projectId, Guid agentId, string userMessage, int maxSteps = 6)
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
            await _modelCallLogger.LogAsync(projectId, agent, callResult, step, "AgentRun");
            var response = callResult.Content;
            if (!_actionParser.TryParse(response, out var action) || action == null)
                return response;
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

                messages.Add(new() { Role = "assistant", Content = response });
                messages.Add(new() { Role = "user", Content = "OBSERVATION:\n" + observation });

                if (observation.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
                    && observation.Contains("0 Error", StringComparison.OrdinalIgnoreCase))
                {
                    await SaveConversation(projectId, agentId, "Build succeeded.");
                    return "Build succeeded.";
                }
            }
        }
        return "Stopped because max steps reached.";
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
