using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Tools.Abstractions;
using ICOGenerator.Services.Tools.Execution;
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
    private readonly ToolPolicyService _toolPolicy;
    private readonly IToolExecutionLogger _toolLogger;
    private readonly AgentPromptBuilder _promptBuilder;
    private readonly WorkspaceTools _workspaceTools;
    private readonly IModelCallLogger _modelCallLogger;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IBudgetGuard _budgetGuard;
    private readonly int _requestTimeoutSeconds;

    public AgentRunService(AppDbContext db, IToolRegistry toolRegistry, ToolPolicyService toolPolicy, IToolExecutionLogger toolLogger, AgentPromptBuilder promptBuilder, WorkspaceTools workspaceTools, IModelCallLogger modelCallLogger, IChatClientFactory chatClientFactory, ILoggerFactory loggerFactory, IBudgetGuard budgetGuard, IConfiguration configuration)
    { _db = db; _toolRegistry = toolRegistry; _toolPolicy = toolPolicy; _toolLogger = toolLogger; _promptBuilder = promptBuilder; _workspaceTools = workspaceTools; _modelCallLogger = modelCallLogger; _chatClientFactory = chatClientFactory; _loggerFactory = loggerFactory; _budgetGuard = budgetGuard; _requestTimeoutSeconds = configuration.GetValue("Llm:RequestTimeoutSeconds", DefaultRequestTimeoutSeconds); }

    // ── Native function-calling path ─────────────────────────────────────────────────────────────────
    // Built on Microsoft Agent Framework: a ChatClientAgent + AgentSession own the ReAct tool loop, so
    // there is no hand-written turn loop here. Cross-cutting concerns are middleware: per-model-call
    // logging/deadline/token-cap is the shared ModelCallLoggingChatClient; each tool is an
    // InvokerBackedAIFunction that validates arguments and layers the per-agent policy + execution
    // logging over the framework's own argument binding + invocation. This method only orchestrates the
    // step budget around the agent run.
    //
    // Budget mirrors a step ceiling driven off the framework's per-request iteration cap, in three phases:
    // (1) run within the expected budget; (2) if it didn't converge, nudge it to finish, granting turns up
    // to the hard cap; (3) if still not done, one tool-free "salvage" turn so a partial result (files
    // already on disk) is summarised instead of lost.
    public async Task<string> RunAsync(Guid projectId, Guid agentId, string userMessage, int maxSteps = 6,
        Action<string, string, string?>? onProgress = null,
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

        var hardCap = maxSteps * AutoContinueFactor;
        var model = agent.AiModel; // guaranteed non-null above.

        // Tools: name + JSON schema + argument binding + invocation all come from AIFunctionFactory (the
        // method signature); InvokerBackedAIFunction adds the per-agent policy, execution logging and the
        // truncated/missing-argument guard. (See InvokerBackedAIFunction.)
        var aiTools = tools
            .Select(t => (AITool)new InvokerBackedAIFunction(
                AIFunctionFactory.Create(t.Method, t.Instance), t, _toolPolicy, _toolLogger, onProgress))
            .ToList();

        // Pipeline: OpenAI client → per-call logging/deadline middleware → function-invocation loop.
        // throwOnFailure: a failed model call ends the run rather than being treated as the agent's
        // final answer.
        var modelClient = new ModelCallLoggingChatClient(
            _chatClientFactory.Create(model), model, _modelCallLogger,
            new ModelCallLogContext(projectId, agent, "AgentRun", workflowRunId),
            _requestTimeoutSeconds, throwOnFailure: true,
            onProgress: onProgress, maxSteps: maxSteps, hardCap: hardCap, budgetGuard: _budgetGuard);
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
