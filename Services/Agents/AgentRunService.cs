using System.Text;
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

namespace ICOGenerator.Services.Agents;

public class AgentRunService
{
    // Returned when the loop exhausts its step budget. Callers compare against this string to detect an incomplete run, so it is part of the contract — keep it in sync.
    public const string MaxStepsReachedResult = "Stopped because max steps reached.";

    // Auto-continue: maxSteps là ngân sách KỲ VỌNG cho một lần chạy. Nếu cạn mà agent vẫn CHƯA tự kết
    // thúc (chưa trả final), ta KHÔNG cắt ngang để "chốt một phần" nữa mà cấp thêm lượt tới trần cứng
    // = maxSteps * AutoContinueFactor, để agent hoàn tất nốt phần còn thiếu (state đã ghi ra đĩa). Trần
    // cứng vẫn cần để không đốt token vô hạn nếu agent không hội tụ; chạm trần mới chạy lượt salvage.
    private const int AutoContinueFactor = 3;

    // Per-call deadline for a model turn (mirrors LlmClient's default); configurable via Llm:RequestTimeoutSeconds.
    private const int DefaultRequestTimeoutSeconds = 600;

    private readonly AppDbContext _db;
    private readonly IToolRegistry _toolRegistry;
    private readonly DynamicToolInvoker _invoker;
    private readonly ILlmClient _llm;
    private readonly AgentPromptBuilder _promptBuilder;
    private readonly AgentActionParser _actionParser;
    private readonly WorkspaceTools _workspaceTools;
    private readonly IModelCallLogger _modelCallLogger;
    private readonly NativeToolCallingPolicy _nativeToolPolicy;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly int _requestTimeoutSeconds;

    public AgentRunService(AppDbContext db, IToolRegistry toolRegistry, DynamicToolInvoker invoker, ILlmClient llm, AgentPromptBuilder promptBuilder, AgentActionParser actionParser, WorkspaceTools workspaceTools, IModelCallLogger modelCallLogger, NativeToolCallingPolicy nativeToolPolicy, IChatClientFactory chatClientFactory, ILoggerFactory loggerFactory, IConfiguration configuration)
    { _db = db; _toolRegistry = toolRegistry; _invoker = invoker; _llm = llm; _promptBuilder = promptBuilder; _actionParser = actionParser; _workspaceTools = workspaceTools; _modelCallLogger = modelCallLogger; _nativeToolPolicy = nativeToolPolicy; _chatClientFactory = chatClientFactory; _loggerFactory = loggerFactory; _requestTimeoutSeconds = configuration.GetValue("Llm:RequestTimeoutSeconds", DefaultRequestTimeoutSeconds); }

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
            ? await RunWithNativeToolsAsync(projectId, agent, tools, userMessage, maxSteps, onProgress, stopWhen, onToken, workflowRunId, cancellationToken)
            : await RunWithPromptProtocolAsync(projectId, agent, tools, userMessage, maxSteps, onProgress, stopWhen, onToken, workflowRunId, cancellationToken);
    }

    // ── Native function-calling path (default) ───────────────────────────────────────────────────────
    // Built on Microsoft Agent Framework: a ChatClientAgent + AgentSession own the ReAct tool loop, so
    // there is no hand-written turn loop here. Cross-cutting concerns are middleware: per-model-call
    // logging/deadline/token-cap is the shared ModelCallLoggingChatClient; each tool is an InvokerBackedAIFunction that
    // validates arguments and routes execution through the shared DynamicToolInvoker (policy + logging +
    // reflection). This method only orchestrates the step budget around the agent run.
    //
    // Budget mirrors the previous loop in three phases driven off the framework's per-request iteration
    // cap: (1) run within the expected budget; (2) if it didn't converge, nudge it to finish, granting
    // turns up to the hard cap; (3) if still not done, one tool-free "salvage" turn so a partial result
    // (files already on disk) is summarised instead of lost. Note: the old in-loop short-circuits
    // (stopWhen, the "Build succeeded" early return) are not ported — they fought the framework-owned loop
    // and stopWhen is unused on this path; the model still stops naturally once the work is done.
    private async Task<string> RunWithNativeToolsAsync(Guid projectId, Agent agent, IReadOnlyList<ToolRuntimeDescriptor> tools,
        string userMessage, int maxSteps, Action<string, string, string?>? onProgress, Func<string, string, bool>? stopWhen,
        Action<string>? onToken, Guid? workflowRunId, CancellationToken cancellationToken)
    {
        var hardCap = maxSteps * AutoContinueFactor;
        var model = agent.AiModel!; // RunAsync guarantees non-null before dispatching here.

        // Tools: name + JSON schema from AIFunctionFactory (the method signature); invocation routed back
        // through DynamicToolInvoker, with the truncated/missing-argument guard. (See InvokerBackedAIFunction.)
        var aiTools = tools
            .Select(t => (AITool)new InvokerBackedAIFunction(
                AIFunctionFactory.Create(t.Method, t.Instance), t, _invoker, onProgress))
            .ToList();

        // Pipeline: OpenAI client → per-call logging/deadline middleware → function-invocation loop.
        // throwOnFailure: a failed model call ends the run (mirrors the old loop) rather than being treated
        // as the agent's final answer.
        var modelClient = new ModelCallLoggingChatClient(
            _chatClientFactory.Create(model), model, _modelCallLogger,
            new ModelCallLogContext(projectId, agent, "AgentRun", workflowRunId),
            _requestTimeoutSeconds, throwOnFailure: true,
            onProgress: onProgress, maxSteps: maxSteps, hardCap: hardCap);
        var functionInvoker = new FunctionInvokingChatClient(modelClient, _loggerFactory)
        {
            MaximumIterationsPerRequest = maxSteps
        };

        var runtimeAgent = new ChatClientAgent(functionInvoker, new ChatClientAgentOptions
        {
            Name = agent.Name,
            ChatOptions = new ChatOptions
            {
                Instructions = _promptBuilder.BuildNative(agent),
                Temperature = (float)agent.Temperature,
                Tools = aiTools
            },
            // We already composed the function-invocation pipeline above; don't let the agent wrap it again.
            UseProvidedChatClientAsIs = true
        }, _loggerFactory);

        var session = await runtimeAgent.CreateSessionAsync(cancellationToken);

        // Phase 1 — run within the expected step budget.
        var (converged, text) = await RunAgentPhaseAsync(runtimeAgent, session, userMessage, modelClient, maxSteps, onToken, runOptions: null, cancellationToken);

        // Phase 2 — auto-continue: budget ran out before the model finished. Nudge it to COMPLETE the
        // remaining work, granting turns up to the hard cap (state is already on disk) so it finishes what
        // it started without burning tokens forever.
        if (!converged)
        {
            functionInvoker.MaximumIterationsPerRequest = hardCap - maxSteps;
            var (continued, continuedText) = await RunAgentPhaseAsync(runtimeAgent, session,
                "Bạn đã dùng hết ngân sách bước dự kiến nhưng công việc dường như CHƯA hoàn tất. "
                + "Hãy tiếp tục để HOÀN THÀNH ĐẦY ĐỦ phần còn thiếu (ví dụ append nốt các phần còn lại của POC), "
                + "tránh các bước thừa, rồi trả lời cuối (không gọi tool) khi đã xong.",
                modelClient, hardCap - maxSteps, onToken, runOptions: null, cancellationToken);
            converged = continued;
            text = continuedText;
        }

        if (converged)
        {
            onProgress?.Invoke("final", "Agent đã hoàn tất công việc.", text);
            await SaveConversation(projectId, agent.Id, text, cancellationToken: cancellationToken);
            return text;
        }

        // Phase 3 — salvage: the hard cap is exhausted but the files the agent wrote are on disk. Give it
        // one tool-free turn (carrying the session history) to summarise what it built, turning a blank
        // failure into a partial completion. Only if that turn produces nothing is the run a real timeout.
        onProgress?.Invoke("thinking", "Đạt giới hạn bước — yêu cầu agent chốt lại kết quả đã hoàn thành.", null);
        var salvageOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            Instructions = _promptBuilder.BuildNative(agent),
            Temperature = (float)agent.Temperature,
            Tools = [] // no tools advertised → a plain summary turn
        });
        var (_, salvageText) = await RunAgentPhaseAsync(runtimeAgent, session,
            "Đã đạt giới hạn số bước xử lý. KHÔNG gọi thêm bất kỳ tool nào nữa. "
            + "Hãy tóm tắt: stack đã chọn, các file/tính năng đã tạo được, cách cài đặt & chạy, và phần nào còn thiếu/chưa hoàn tất.",
            modelClient, int.MaxValue, onToken, salvageOptions, cancellationToken);

        if (!string.IsNullOrWhiteSpace(salvageText))
        {
            onProgress?.Invoke("final", "Agent đã chốt kết quả (một phần) khi đạt giới hạn bước.", salvageText);
            await SaveConversation(projectId, agent.Id, salvageText, cancellationToken: cancellationToken);
            return salvageText;
        }

        onProgress?.Invoke("final", "Dừng do đạt giới hạn số bước xử lý.", null);
        return MaxStepsReachedResult;
    }

    // Runs one agent invocation on the shared session, streaming text deltas to onToken. "Converged" means
    // the agent finished UNDER its iteration budget (the model answered without asking for more tools);
    // using the whole budget means it was cut off mid-work and a follow-up/salvage turn is needed.
    private static async Task<(bool converged, string text)> RunAgentPhaseAsync(
        ChatClientAgent runtimeAgent, AgentSession session, string message, ModelCallLoggingChatClient modelClient,
        int budget, Action<string>? onToken, ChatClientAgentRunOptions? runOptions, CancellationToken cancellationToken)
    {
        var startStep = modelClient.StepCount;
        var builder = new StringBuilder();
        await foreach (var update in runtimeAgent.RunStreamingAsync(message, session, runOptions, cancellationToken))
        {
            var delta = update.Text;
            if (string.IsNullOrEmpty(delta))
                continue;

            builder.Append(delta);
            // A misbehaving sink must never break the run, so swallow anything it throws.
            if (onToken != null)
            {
                try { onToken(delta); }
                catch { /* ignore UI streaming failures */ }
            }
        }

        var used = modelClient.StepCount - startStep;
        return (used < budget, builder.ToString());
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

        var hardCap = maxSteps * AutoContinueFactor;
        var wrapUpNudged = false;
        for (var step = 1; step <= hardCap; step++)
        {
            var budgetLabel = step <= maxSteps ? $"{step}/{maxSteps}" : $"{step}/{hardCap} (chạy thêm để hoàn tất)";
            onProgress?.Invoke("thinking", $"Agent {agent.Name} đang suy nghĩ… (bước {budgetLabel})", null);

            // Vượt ngân sách kỳ vọng lần đầu: nhắc agent tập trung HOÀN TẤT phần còn thiếu rồi kết thúc.
            if (step == maxSteps + 1 && !wrapUpNudged)
            {
                wrapUpNudged = true;
                messages.Add(new() { Role = "user", Content =
                    "Bạn đã dùng hết ngân sách bước dự kiến nhưng công việc dường như CHƯA hoàn tất. "
                    + "Hãy tiếp tục để HOÀN THÀNH ĐẦY ĐỦ phần còn thiếu, tránh các bước thừa, "
                    + "rồi trả về JSON {\"type\":\"final\",\"content\":\"...\"} khi đã xong." });
            }

            // Logging now lives in the shared middleware (inside ChatWithLogAsync); pass the step/purpose via context.
            var callResult = await _llm.ChatWithLogAsync(agent.AiModel!, messages, agent.Temperature,
                new ModelCallLogContext(projectId, agent, "AgentRun", workflowRunId, step), onToken, cancellationToken);

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
                if (reformatNudges < maxReformatNudges && step < hardCap)
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

        var salvageCall = await _llm.ChatWithLogAsync(agent.AiModel!, messages, agent.Temperature,
            new ModelCallLogContext(projectId, agent, "AgentRun", workflowRunId, hardCap + 1), onToken, cancellationToken);
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
