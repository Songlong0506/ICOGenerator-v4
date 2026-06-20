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
    // Returned when the loop exhausts its step budget. Callers compare against this string to detect an incomplete run, so it is part of the contract — keep it in sync.
    public const string MaxStepsReachedResult = "Stopped because max steps reached.";

    // How many steps before the budget runs out we start reminding the agent to wrap up and return a
    // `final` summary, so a long multi-file job (e.g. full code-gen) hands off in time instead of
    // spending its last steps writing yet another file and stalling the run at the limit.
    private const int FinalizeWarningWindow = 3;

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
        Action<string>? onToken = null, Guid? workflowRunId = null, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FindAsync([projectId], cancellationToken) ?? throw new InvalidOperationException("Project not found.");
        var agent = await _db.Agents.Include(x => x.AiModel).FirstAsync(x => x.Id == agentId, cancellationToken);
        if (agent.AiModel == null) throw new InvalidOperationException("Agent model is not configured.");
        _workspaceTools.SetWorkspace(WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));
        // Surface this run's token to tools (CommandTools resolves the same scoped WorkspaceTools) so a
        // cancel/shutdown kills any spawned process instead of letting it run to the command timeout.
        _workspaceTools.SetRunCancellation(cancellationToken);
        var tools = await _toolRegistry.GetToolsForAgentAsync(agentId);
        var messages = new List<ChatMessageDto>
        {
            new() { Role = "system", Content = _promptBuilder.Build(agent, tools) },
            new() { Role = "user", Content = userMessage }
        };

        // Số lần tối đa nhắc model định dạng lại khi nó trả văn xuôi thay vì một JSON action,
        // trước khi chấp nhận phản hồi đó là câu trả lời cuối (tránh nhắc vô hạn).
        const int maxReformatNudges = 2;
        var reformatNudges = 0;

        // When the budget runs out, an agent that finishes via a `final` summary (full code-gen,
        // architecture, testing) should still hand off what it built rather than have the whole run
        // discarded. POC is excluded: it gates completion on stopWhen (SetPocContent succeeding), so
        // exhausting the budget there genuinely means the demo was never produced.
        var canFinalizeOnExhaustion = stopWhen == null;
        // Did the agent actually write anything to the workspace? Gates the finalize-on-exhaustion
        // fallback so we never synthesise a "done" summary for a run that produced no deliverable.
        var wroteFiles = false;

        for (var step = 1; step <= maxSteps; step++)
        {
            onProgress?.Invoke("thinking", $"Agent {agent.Name} đang suy nghĩ… (bước {step}/{maxSteps})", null);
            var callResult = await _llm.ChatWithLogAsync(agent.AiModel, messages, agent.Temperature, onToken, cancellationToken);
            await _modelCallLogger.LogAsync(projectId, agent, callResult, step, "AgentRun", workflowRunId);

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
                // Model trả về văn xuôi thay vì một JSON action (hay gặp ở model yếu:
                // "I'll create the file…" mà không gọi tool). Thay vì coi đó là "đã xong"
                // và bỏ qua việc dùng tool, nhắc model định dạng lại rồi thử tiếp — chỉ chấp
                // nhận phản hồi là câu trả lời cuối sau khi đã nhắc tối đa số lần cho phép.
                if (reformatNudges < maxReformatNudges && step < maxSteps)
                {
                    reformatNudges++;
                    onProgress?.Invoke("thinking", "Phản hồi chưa đúng định dạng — nhắc agent trả JSON action.", null);
                    messages.Add(new() { Role = "assistant", Content = response });
                    messages.Add(new() { Role = "user", Content =
                        "Phản hồi vừa rồi KHÔNG hợp lệ: đó là văn xuôi, không phải một JSON action. "
                        + "Hãy trả về DUY NHẤT một JSON object (không kèm chữ nào khác, không markdown) theo đúng MỘT trong hai dạng:\n"
                        + "{\"type\":\"tool\",\"tool\":\"TenTool\",\"args\":{...}}\n"
                        + "hoặc {\"type\":\"final\",\"content\":\"...\"}\n"
                        + "Nếu task yêu cầu ghi/đọc file thì gọi tool tương ứng NGAY (vd WriteFile) thay vì mô tả ý định." });
                    continue;
                }

                onProgress?.Invoke("final", "Agent đã trả lời.", response);
                return response;
            }

            reformatNudges = 0; // có action hợp lệ → reset bộ đếm nhắc
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
                        // Feed a recoverable tool failure back as an observation so the model can correct itself instead of aborting the run; unwrap the reflection wrapper for a useful message.
                        var real = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
                        observation = $"ERROR: {real.Message}";
                    }
                }

                onProgress?.Invoke("observation", $"Đã nhận kết quả từ {action.Tool}", observation);

                // Track real deliverables (matches WorkspaceTools' WriteFile/ReplaceInFile output) so the
                // finalize-on-exhaustion fallback below only fires when the agent actually produced something.
                if (observation.StartsWith("File written:", StringComparison.Ordinal)
                    || observation.StartsWith("File updated:", StringComparison.Ordinal))
                    wroteFiles = true;

                // Stop as soon as the caller's success condition is met so a weak model doesn't keep making spurious edits and burn the step budget after the work is done.
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

                // Approaching the budget: piggyback a wrap-up reminder onto this OBSERVATION so the agent
                // returns a final summary while it still has a turn left, instead of writing one more file.
                var stepsLeft = maxSteps - step;
                if (canFinalizeOnExhaustion && stepsLeft is > 0 and <= FinalizeWarningWindow)
                    messages[^1].Content +=
                        $"\n\n[HỆ THỐNG] Chỉ còn {stepsLeft} bước. Hãy hoàn tất nốt phần cốt lõi rồi trả về NGAY một action "
                        + "{\"type\":\"final\",\"content\":\"...\"} tóm tắt (stack, các file chính đã tạo, cách chạy, phần còn hạn chế). "
                        + "Đừng để hết bước mà chưa trả final.";
            }
        }

        // Budget exhausted without a `final`. If the agent finishes via a summary (not POC) and actually
        // wrote files, give it ONE dedicated turn — no tools — to hand off what it built, so a finished-
        // but-unsummarised codebase isn't discarded as a hard failure. One bounded call beyond the budget.
        if (canFinalizeOnExhaustion && wroteFiles)
        {
            onProgress?.Invoke("thinking", "Đã đạt giới hạn số bước — yêu cầu agent tóm tắt kết quả.", null);
            messages.Add(new() { Role = "user", Content =
                "Bạn đã đạt giới hạn số bước và KHÔNG còn được gọi tool nữa. "
                + "Hãy trả về DUY NHẤT một JSON action {\"type\":\"final\",\"content\":\"...\"} tóm tắt những gì đã hiện thực: "
                + "stack đã dùng, danh sách các file chính đã tạo, cách chạy, và phần còn hạn chế. "
                + "Bản tóm tắt này sẽ được chuyển cho bước kế tiếp." });

            var finalizeResult = await _llm.ChatWithLogAsync(agent.AiModel, messages, agent.Temperature, onToken, cancellationToken);
            await _modelCallLogger.LogAsync(projectId, agent, finalizeResult, maxSteps + 1, "AgentRun", workflowRunId);

            if (finalizeResult.IsSuccess)
            {
                var summary = _actionParser.TryParse(finalizeResult.Content, out var finalAction)
                    && finalAction != null
                    && finalAction.Type.Equals("final", StringComparison.OrdinalIgnoreCase)
                        ? finalAction.Content ?? finalizeResult.Content
                        : finalizeResult.Content;

                if (!string.IsNullOrWhiteSpace(summary))
                {
                    onProgress?.Invoke("final", "Agent đã chốt kết quả sau khi đạt giới hạn số bước.", summary);
                    await SaveConversation(projectId, agentId, summary, cancellationToken: cancellationToken);
                    return summary;
                }
            }
        }

        onProgress?.Invoke("final", "Dừng do đạt giới hạn số bước xử lý.", null);
        return MaxStepsReachedResult;
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
