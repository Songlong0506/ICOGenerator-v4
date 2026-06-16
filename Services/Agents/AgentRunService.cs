using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Tools.Registry;
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

    public async Task<string> RunAsync(Guid projectId, Guid agentId, string userMessage, int maxSteps = 6,
        Action<string, string, string?>? onProgress = null, Func<string, string, bool>? stopWhen = null,
        CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FindAsync([projectId], cancellationToken) ?? throw new InvalidOperationException("Project not found.");
        var agent = await _db.Agents.Include(x => x.AiModel).FirstAsync(x => x.Id == agentId, cancellationToken);
        if (agent.AiModel == null) throw new InvalidOperationException("Agent model is not configured.");
        _workspaceTools.SetWorkspace(WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));
        var tools = await _toolRegistry.GetToolsForAgentAsync(agentId);
        var messages = new List<ChatMessageDto>
        {
            new() { Role = "system", Content = _promptBuilder.Build(agent, tools) },
            new() { Role = "user", Content = userMessage }
        };

        for (var step = 1; step <= maxSteps; step++)
        {
            onProgress?.Invoke("thinking", $"Agent {agent.Name} đang suy nghĩ… (bước {step}/{maxSteps})", null);
            var callResult = await _llm.ChatWithLogAsync(agent.AiModel, messages, agent.Temperature, cancellationToken);
            await _modelCallLogger.LogAsync(projectId, agent, callResult, step, "AgentRun");

            // A failed LLM call (HTTP error / timeout) must not be treated as the agent's
            // final answer — surface it so the task is marked Failed instead of "done".
            if (!callResult.IsSuccess)
            {
                var detail = callResult.ErrorMessage ?? callResult.Content;
                onProgress?.Invoke("error", "Lời gọi LLM thất bại.", detail);
                throw new InvalidOperationException($"LLM call failed: {detail}");
            }

            var response = callResult.Content;
            if (!_actionParser.TryParse(response, out var action) || action == null)
            {
                onProgress?.Invoke("final", "Agent đã trả lời.", response);
                return response;
            }
            if (action.Type.Equals("final", StringComparison.OrdinalIgnoreCase))
            {
                onProgress?.Invoke("final", "Agent đã hoàn tất công việc.", action.Content ?? response);
                await SaveConversation(projectId, agentId, action.Content ?? response, cancellationToken: cancellationToken);
                return action.Content ?? response;
            }
            if (action.Type.Equals("tool", StringComparison.OrdinalIgnoreCase))
            {
                var tool = tools.FirstOrDefault(x =>
                    x.Definition.Name.Equals(action.Tool, StringComparison.OrdinalIgnoreCase));

                onProgress?.Invoke("tool", $"Đang dùng tool: {action.Tool}", DescribeToolArgs(action.Args));

                string observation;
                if (tool == null)
                {
                    observation = $"Tool not found: {action.Tool}";
                }
                else
                {
                    try
                    {
                        observation = await _invoker.InvokeAsync(tool, action.Args);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // A recoverable tool failure (bad args, disallowed file extension,
                        // missing file…) must be fed back to the model as an observation so
                        // it can correct itself, instead of aborting the whole run and marking
                        // the task Failed. Unwrap the reflection wrapper for a useful message.
                        var real = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
                        observation = $"ERROR: {real.Message}";
                    }
                }

                onProgress?.Invoke("observation", $"Đã nhận kết quả từ {action.Tool}", observation);

                // Stop as soon as the caller's success condition is met (e.g. the POC
                // content edit landed) so a weak model doesn't keep making spurious extra
                // edits and burn through the step budget after the work is already done.
                if (stopWhen != null && stopWhen(action.Tool ?? string.Empty, observation))
                {
                    onProgress?.Invoke("final", "Agent đã hoàn tất công việc.", observation);
                    await SaveConversation(projectId, agentId, observation, cancellationToken: cancellationToken);
                    return observation;
                }

                messages.Add(new() { Role = "assistant", Content = response });
                messages.Add(new() { Role = "user", Content = "OBSERVATION:\n" + observation });

                if (observation.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
                    && observation.Contains("0 Error", StringComparison.OrdinalIgnoreCase))
                {
                    onProgress?.Invoke("final", "Build succeeded.", null);
                    await SaveConversation(projectId, agentId, "Build succeeded.", cancellationToken: cancellationToken);
                    return "Build succeeded.";
                }
            }
        }
        onProgress?.Invoke("final", "Dừng do đạt giới hạn số bước xử lý.", null);
        return "Stopped because max steps reached.";
    }

    private static string? DescribeToolArgs(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        if (args.Count == 0)
            return null;

        var parts = args.Select(kv =>
        {
            var value = kv.Value.ToString();
            if (value.Length > 80) value = value[..80] + "…";
            return $"{kv.Key}: {value}";
        });

        return string.Join("\n", parts);
    }

    private async Task SaveConversation(Guid projectId, Guid agentId, string message, string role = "assistant", CancellationToken cancellationToken = default)
    {
        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = agentId,
            Role = role,
            Message = message,
            TokenUsed = TokenEstimator.Estimate(message)
        });

        await _db.SaveChangesAsync(cancellationToken);
    }
}
