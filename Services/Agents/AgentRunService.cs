using System.Text.Json;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Tools.Registry;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Logging;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ICOGenerator.Services.Agents;

public class AgentRunService
{
    // Returned when the loop exhausts its step budget. Callers compare against this string to detect an incomplete run, so it is part of the contract — keep it in sync.
    public const string MaxStepsReachedResult = "Stopped because max steps reached.";

    private readonly AppDbContext _db;
    private readonly IToolRegistry _toolRegistry;
    private readonly DynamicToolInvoker _invoker;
    private readonly ILlmClient _llm;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly AgentPromptBuilder _promptBuilder;
    private readonly AgentActionParser _actionParser;
    private readonly WorkspaceTools _workspaceTools;
    private readonly IModelCallLogger _modelCallLogger;
    private readonly NativeToolCallingPolicy _nativeToolPolicy;
    private readonly ILoggerFactory _loggerFactory;

    public AgentRunService(AppDbContext db, IToolRegistry toolRegistry, DynamicToolInvoker invoker, ILlmClient llm, IChatClientFactory chatClientFactory, AgentPromptBuilder promptBuilder, AgentActionParser actionParser, WorkspaceTools workspaceTools, IModelCallLogger modelCallLogger, NativeToolCallingPolicy nativeToolPolicy, ILoggerFactory loggerFactory)
    { _db = db; _toolRegistry = toolRegistry; _invoker = invoker; _llm = llm; _chatClientFactory = chatClientFactory; _promptBuilder = promptBuilder; _actionParser = actionParser; _workspaceTools = workspaceTools; _modelCallLogger = modelCallLogger; _nativeToolPolicy = nativeToolPolicy; _loggerFactory = loggerFactory; }

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

        // Default to the model's native tool-calling; only models explicitly configured as not supporting
        // the OpenAI "tools" parameter fall back to the prompt-based JSON-action protocol.
        return _nativeToolPolicy.UseNativeTools(agent.AiModel)
            ? await RunWithNativeToolsAsync(projectId, agent, tools, userMessage, maxSteps, onProgress, stopWhen, workflowRunId, cancellationToken)
            : await RunWithPromptProtocolAsync(projectId, agent, tools, userMessage, maxSteps, onProgress, stopWhen, onToken, workflowRunId, cancellationToken);
    }

    // ── Native function-calling path (default) ───────────────────────────────────────────────────────
    // The agent loop itself (advertise tools → model turn → run tool calls → feed results back → repeat)
    // is owned by Microsoft Agent Framework's ChatClientAgent. Every app-specific concern is layered onto
    // the MAF pipeline rather than hand-rolled:
    //   • tool invocation is routed back through DynamicToolInvoker via the FunctionInvoker hook, so the
    //     per-agent tool policy + tool-execution logging are unchanged;
    //   • per-round model-call logging, the "thinking" progress event and per-round token caps live in
    //     AgentRunInstrumentationChatClient (sits beneath the function-invocation layer);
    //   • maxSteps maps to MaximumIterationsPerRequest;
    //   • stopWhen and a clean build short-circuit the loop via FunctionInvocationContext.Terminate.
    // Token-level streaming (onToken) is intentionally not used here, matching the previous behaviour.
    private async Task<string> RunWithNativeToolsAsync(Guid projectId, Agent agent, IReadOnlyList<ToolRuntimeDescriptor> tools,
        string userMessage, int maxSteps, Action<string, string, string?>? onProgress, Func<string, string, bool>? stopWhen,
        Guid? workflowRunId, CancellationToken cancellationToken)
    {
        const string buildSucceededResult = "Build succeeded.";

        var aiTools = new List<AITool>(tools.Count);
        var descriptorsByName = new Dictionary<string, ToolRuntimeDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            aiTools.Add(AIFunctionFactory.Create(tool.Method, tool.Instance));
            descriptorsByName[tool.Definition.Name] = tool;
        }

        // Set when stopWhen / a clean build short-circuits the loop: holds the result to return and marks
        // that the run finished early (rather than the model emitting its own final text answer).
        string? earlyResult = null;

        // Invoked by MAF for each tool the model calls. Routes through DynamicToolInvoker (policy + log +
        // reflection) exactly as the old loop did, emits the tool/observation progress events, and applies
        // the two early-exits by setting context.Terminate.
        async ValueTask<object?> InvokeToolAsync(FunctionInvocationContext context, CancellationToken toolCt)
        {
            var name = context.Function.Name;
            var args = ToJsonArgs(context.Arguments);
            onProgress?.Invoke("tool", $"Đang dùng tool: {name}", DescribeToolArgs(args));

            string observation;
            if (!descriptorsByName.TryGetValue(name, out var descriptor))
            {
                observation = $"Tool not found: {name}";
            }
            else
            {
                try
                {
                    observation = await _invoker.InvokeAsync(descriptor, args);
                }
                catch (OperationCanceledException) when (toolCt.IsCancellationRequested)
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

            onProgress?.Invoke("observation", $"Đã nhận kết quả từ {name}", observation);

            // Stop as soon as the caller's success condition is met so a weak model doesn't keep making
            // spurious edits and burn the step budget after the work is done.
            if (stopWhen != null && stopWhen(name, observation))
            {
                earlyResult = observation;
                context.Terminate = true;
            }
            else if (observation.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
                     && observation.Contains("0 Error", StringComparison.OrdinalIgnoreCase))
            {
                earlyResult = buildSucceededResult;
                context.Terminate = true;
            }

            return observation;
        }

        // base model client → instrumentation (per-round log/progress/token cap) → function invocation.
        var chatClient = _chatClientFactory.Create(agent.AiModel)
            .AsBuilder()
            .UseFunctionInvocation(_loggerFactory, fic =>
            {
                fic.MaximumIterationsPerRequest = maxSteps;
                fic.IncludeDetailedErrors = true;
                fic.FunctionInvoker = InvokeToolAsync;
            })
            .Use(inner => new AgentRunInstrumentationChatClient(
                inner, _modelCallLogger, agent.AiModel, agent, projectId, workflowRunId, maxSteps, agent.Temperature, onProgress))
            .Build();

        var agentRunner = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = agent.Name,
            ChatOptions = new ChatOptions
            {
                Instructions = _promptBuilder.BuildNative(agent),
                Temperature = (float)agent.Temperature,
                Tools = aiTools,
                ToolMode = ChatToolMode.Auto
            },
            // We've already built the exact pipeline we want; don't let MAF add its own function-invocation layer.
            UseProvidedChatClientAsIs = true
        }, _loggerFactory);

        var session = await agentRunner.CreateSessionAsync(cancellationToken);

        AgentResponse response;
        try
        {
            ChatClientAgentRunOptions? runOptions = null;
            response = await agentRunner.RunAsync(userMessage, session, runOptions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed LLM call (HTTP error / timeout) must not be treated as the agent's final answer —
            // surface it so the task is marked Failed instead of "done".
            onProgress?.Invoke("error", "Lời gọi LLM thất bại.", ex.Message);
            throw new InvalidOperationException($"LLM call failed: {ex.Message}", ex);
        }

        // stopWhen / clean-build short-circuit: return the stashed tool result, not the model's (absent) reply.
        if (earlyResult is not null)
        {
            var isBuild = earlyResult == buildSucceededResult;
            onProgress?.Invoke("final", isBuild ? "Build succeeded." : "Agent đã hoàn tất công việc.", isBuild ? null : earlyResult);
            await SaveConversation(projectId, agent.Id, earlyResult, cancellationToken: cancellationToken);
            return earlyResult;
        }

        // A plain reply (the model stopped calling tools) is a legitimate "done".
        var finalText = response.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            onProgress?.Invoke("final", "Agent đã hoàn tất công việc.", finalText);
            await SaveConversation(projectId, agent.Id, finalText, cancellationToken: cancellationToken);
            return finalText;
        }

        // Ngân sách bước đã cạn nhưng các file agent đã ghi vẫn nằm trên đĩa. Cho agent ĐÚNG MỘT lượt cuối
        // (ToolMode.None ⇒ không gọi tool) để chốt một final tóm tắt — biến "fail trắng vứt cả phần đã làm"
        // thành "hoàn tất một phần". Chỉ khi lượt này vẫn không ra nội dung mới coi là chạm-giới-hạn thật.
        onProgress?.Invoke("thinking", "Đạt giới hạn bước — yêu cầu agent chốt lại kết quả đã hoàn thành.", null);
        try
        {
            var salvageOptions = new ChatClientAgentRunOptions(new ChatOptions { ToolMode = ChatToolMode.None });
            var salvage = await agentRunner.RunAsync(
                "Đã đạt giới hạn số bước xử lý. KHÔNG gọi thêm bất kỳ tool nào nữa. "
                + "Hãy tóm tắt: stack đã chọn, các file/tính năng đã tạo được, cách cài đặt & chạy, và phần nào còn thiếu/chưa hoàn tất.",
                session, salvageOptions, cancellationToken);

            if (!string.IsNullOrWhiteSpace(salvage.Text))
            {
                onProgress?.Invoke("final", "Agent đã chốt kết quả (một phần) khi đạt giới hạn bước.", salvage.Text);
                await SaveConversation(projectId, agent.Id, salvage.Text, cancellationToken: cancellationToken);
                return salvage.Text;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Salvage is best-effort; fall through to the max-steps result.
        }

        onProgress?.Invoke("final", "Dừng do đạt giới hạn số bước xử lý.", null);
        return MaxStepsReachedResult;
    }

    // ── Prompt-based JSON-action path (fallback for models without native tool-calling) ───────────────
    private async Task<string> RunWithPromptProtocolAsync(Guid projectId, Agent agent, IReadOnlyList<ToolRuntimeDescriptor> tools,
        string userMessage, int maxSteps, Action<string, string, string?>? onProgress, Func<string, string, bool>? stopWhen,
        Action<string>? onToken, Guid? workflowRunId, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessageDto>
        {
            new() { Role = "system", Content = _promptBuilder.Build(agent, tools) },
            new() { Role = "user", Content = userMessage }
        };

        // Số lần tối đa nhắc model định dạng lại khi nó trả văn xuôi thay vì một JSON action,
        // trước khi chấp nhận phản hồi đó là câu trả lời cuối (tránh nhắc vô hạn).
        const int maxReformatNudges = 2;
        var reformatNudges = 0;

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
                await SaveConversation(projectId, agent.Id, action.Content ?? response, cancellationToken: cancellationToken);
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

                // Stop as soon as the caller's success condition is met so a weak model doesn't keep making spurious edits and burn the step budget after the work is done.
                if (stopWhen != null && stopWhen(action.Tool ?? string.Empty, observation))
                {
                    onProgress?.Invoke("final", "Agent đã hoàn tất công việc.", observation);
                    await SaveConversation(projectId, agent.Id, observation, cancellationToken: cancellationToken);
                    return observation;
                }

                messages.Add(new() { Role = "assistant", Content = response });
                messages.Add(new() { Role = "user", Content = "OBSERVATION:\n" + observation });

                if (observation.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
                    && observation.Contains("0 Error", StringComparison.OrdinalIgnoreCase))
                {
                    onProgress?.Invoke("final", "Build succeeded.", null);
                    await SaveConversation(projectId, agent.Id, "Build succeeded.", cancellationToken: cancellationToken);
                    return "Build succeeded.";
                }
            }
        }
        // Ngân sách bước đã cạn nhưng các file agent đã ghi vẫn nằm trên đĩa. Cho agent ĐÚNG MỘT lượt
        // cuối (không được gọi tool) để chốt một final tóm tắt những gì đã làm — biến "fail trắng vứt cả
        // phần đã làm" thành "hoàn tất một phần". Chỉ khi lượt này vẫn không ra final hợp lệ mới coi là
        // chạm-giới-hạn thật (trả MaxStepsReachedResult để caller đánh Failed như cũ).
        onProgress?.Invoke("thinking", "Đạt giới hạn bước — yêu cầu agent chốt lại kết quả đã hoàn thành.", null);
        messages.Add(new() { Role = "user", Content =
            "Đã đạt giới hạn số bước xử lý. KHÔNG gọi thêm bất kỳ tool nào nữa. "
            + "Hãy trả về DUY NHẤT một JSON {\"type\":\"final\",\"content\":\"...\"} (không kèm chữ nào khác, không markdown) "
            + "tóm tắt: stack đã chọn, các file/tính năng đã tạo được, cách cài đặt & chạy, và phần nào còn thiếu/chưa hoàn tất." });

        var salvageCall = await _llm.ChatWithLogAsync(agent.AiModel, messages, agent.Temperature, onToken, cancellationToken);
        await _modelCallLogger.LogAsync(projectId, agent, salvageCall, maxSteps + 1, "AgentRun", workflowRunId);
        if (salvageCall.IsSuccess
            && _actionParser.TryParse(salvageCall.Content, out var salvageAction)
            && salvageAction != null
            && salvageAction.Type.Equals("final", StringComparison.OrdinalIgnoreCase))
        {
            var content = salvageAction.Content ?? salvageCall.Content;
            onProgress?.Invoke("final", "Agent đã chốt kết quả (một phần) khi đạt giới hạn bước.", content);
            await SaveConversation(projectId, agent.Id, content, cancellationToken: cancellationToken);
            return content;
        }

        onProgress?.Invoke("final", "Dừng do đạt giới hạn số bước xử lý.", null);
        return MaxStepsReachedResult;
    }

    // Converts the model's native tool-call arguments (object? values, usually already JsonElement) into
    // the JsonElement map DynamicToolInvoker binds to method parameters, so both agent paths share one
    // invocation/logging/reflection code path.
    private static Dictionary<string, JsonElement> ToJsonArgs(IDictionary<string, object?>? args)
    {
        var result = new Dictionary<string, JsonElement>();
        if (args == null)
            return result;

        foreach (var (key, value) in args)
            result[key] = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value);

        return result;
    }

    private static string? DescribeToolArgs(Dictionary<string, JsonElement> args)
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
