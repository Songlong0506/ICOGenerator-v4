using System.Text;
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Domain.Enums;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Budget;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Requirements;
using ICOGenerator.Services.Tools.Abstractions;
using ICOGenerator.Services.Tools.Execution;
using ICOGenerator.Services.Tools.Registry;
using ICOGenerator.Services.Tools;
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

    // Kết quả điền cho lời gọi tool mà vòng lặp BỎ LẠI khi một pha cạn ngân sách: lượt trả lời cuối của pha
    // vẫn có thể xin tool, nhưng vòng lặp dừng trước khi chạy nó. Để nguyên thì hội thoại mang một lời gọi
    // tool không có kết quả ngay trước lời nhắc của pha kế — endpoint chặt chẽ từ chối cả request, endpoint
    // dễ dãi thì để model tưởng tool đã chạy. Khác với AgentRunCheckpointStore.InterruptedToolResult: ở đây
    // ta BIẾT chắc tool chưa chạy.
    public const string SkippedToolResult =
        "CHƯA CHẠY: lời gọi tool này không được thực thi vì đã hết ngân sách bước của pha trước. "
        + "Nếu vẫn cần thì gọi lại.";

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
    private readonly LlmSettings _llmSettings;
    private readonly ChecklistNoteStore _checklistNotes;
    private readonly AgentRunCheckpointStore _checkpoints;

    public AgentRunService(AppDbContext db, IToolRegistry toolRegistry, ToolPolicyService toolPolicy, IToolExecutionLogger toolLogger, AgentPromptBuilder promptBuilder, WorkspaceTools workspaceTools, IModelCallLogger modelCallLogger, IChatClientFactory chatClientFactory, ILoggerFactory loggerFactory, IBudgetGuard budgetGuard, LlmSettings llmSettings, ChecklistNoteStore checklistNotes, AgentRunCheckpointStore checkpoints)
    { _db = db; _toolRegistry = toolRegistry; _toolPolicy = toolPolicy; _toolLogger = toolLogger; _promptBuilder = promptBuilder; _workspaceTools = workspaceTools; _modelCallLogger = modelCallLogger; _chatClientFactory = chatClientFactory; _loggerFactory = loggerFactory; _budgetGuard = budgetGuard; _llmSettings = llmSettings; _checklistNotes = checklistNotes; _checkpoints = checkpoints; }

    // ── Native function-calling path ─────────────────────────────────────────────────────────────────
    // Built on Microsoft Agent Framework: a ChatClientAgent + AgentSession own the ReAct tool loop, so
    // there is no hand-written turn loop here. Cross-cutting concerns are middleware: per-model-call
    // logging/deadline/token-cap is the shared ModelCallLoggingChatClient; each tool is an
    // InvokerBackedAIFunction that validates arguments and layers the per-agent policy + execution
    // logging over the framework's own argument binding + invocation. This method only orchestrates the
    // step budget around the agent run.
    //
    // Budget is a step ceiling counted on real model calls (PhaseStepBudget), in three phases:
    // (1) run within the expected budget; (2) if it didn't converge, nudge it to finish, granting turns up
    // to the hard cap (tắt được qua AgentRunOverrides.AutoContinue); (3) if still not done, one tool-free
    // "salvage" turn so a partial result (files already on disk) is summarised instead of lost.
    //
    // Checkpoint (tham số `checkpoint`): có ⇒ hội thoại được ghi bền quanh mỗi lời gọi model
    // (CheckpointingChatClient), và nếu nó mang ResumeFrom thì lượt này CHẠY TIẾP hội thoại đó thay vì bắt
    // đầu từ `userMessage` — số bước đã tiêu ở lần trước được trừ vào ngân sách, nên một lượt chạy tiếp
    // không được cấp lại nguyên ngân sách bước. Không truyền ⇒ hành vi y như trước (không ghi gì).
    public async Task<string> RunAsync(Guid projectId, Guid agentId, string userMessage, int maxSteps = 6,
        Action<string, string, string?>? onProgress = null,
        Action<string>? onToken = null, Guid? workflowRunId = null, CancellationToken cancellationToken = default,
        AgentRunOverrides? overrides = null, AgentRunCheckpointScope? checkpoint = null)
    {
        var project = await _db.Projects.FindAsync([projectId], cancellationToken) ?? throw new InvalidOperationException("Project not found.");
        var agent = await _db.Agents.Include(x => x.AiModel).FirstAsync(x => x.Id == agentId, cancellationToken);
        // Model của lượt này: ghi đè (thử model, xem AgentRunOverrides) hoặc model gán cho agent.
        var model = overrides?.Model ?? agent.AiModel
            ?? throw new InvalidOperationException("Agent model is not configured.");
        if (overrides?.WorkspacePath is { } workspacePath)
            _workspaceTools.SetWorkspacePath(workspacePath);
        else
            _workspaceTools.SetWorkspace(WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));
        // Surface this run's token to tools (CommandTools resolves the same scoped WorkspaceTools) so a
        // cancel/shutdown kills any spawned process instead of letting it run to the command timeout.
        _workspaceTools.SetRunCancellation(cancellationToken);
        // Cho các tool POC tường thuật milestone "đã dựng màn hình X" qua chính sink tiến độ của run này.
        _workspaceTools.SetProgressSink(onProgress);
        var tools = await _toolRegistry.GetToolsForAgentAsync(agentId);
        if (overrides?.AllowedTools is { } allowed)
            tools = tools.Where(t => allowed.Contains(t.Definition.Name, StringComparer.Ordinal)).ToList();

        // Bài học vai này đã rút từ nhận xét của người duyệt ở các dự án TRƯỚC (StageRevisionMemoryService).
        // Chỉ bucket CHUNG — bài học kỹ thuật không phụ thuộc phòng ban, xem ChecklistNoteStore. Đọc MỘT lần
        // ở đây rồi dùng cho cả lượt chính lẫn lượt salvage: BuildNative được gọi hai chỗ, và một truy vấn
        // thêm cho lượt salvage chỉ để dựng lại đúng chuỗi vừa có là phí. Vai chưa học gì ⇒ null ⇒ prompt
        // giữ nguyên như trước.
        var learnedChecklist = await _checklistNotes.BuildForChatAsync(agent, null, cancellationToken);

        // Caller tắt auto-continue (nó tự có vòng lặp ngoài, vd cổng UAT) ⇒ trần cứng = chính ngân sách kỳ
        // vọng: cạn là sang thẳng lượt salvage, không có pha "làm tiếp" nhân ba số bước.
        var autoContinue = overrides?.AutoContinue ?? true;
        var hardCap = autoContinue ? maxSteps * AutoContinueFactor : maxSteps;

        // Chạy tiếp: số bước đã tiêu ở (các) lần chạy trước. Đã vượt ngân sách kỳ vọng ⇒ lượt trước chết
        // khi đang ở pha auto-continue, nên lượt này vào thẳng pha đó với phần trần cứng còn lại.
        var resume = checkpoint?.ResumeFrom;
        var stepsBefore = resume?.CompletedSteps ?? 0;
        var resumedInAutoContinue = autoContinue && stepsBefore >= maxSteps;
        var firstBudget = resumedInAutoContinue ? Math.Max(hardCap - stepsBefore, 1) : maxSteps - stepsBefore;

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
            new ModelCallLogContext(projectId, agent, overrides?.Purpose ?? "AgentRun", workflowRunId),
            new ModelCallOptions(_llmSettings.RequestTimeoutSeconds, ThrowOnFailure: true)
            {
                OnProgress = onProgress,
                MaxSteps = maxSteps,
                HardCap = hardCap,
                StartStep = stepsBefore,
                BudgetGuard = _budgetGuard
            });
        // Checkpoint đứng NGAY DƯỚI vòng lặp gọi tool: ở đó nó thấy đúng danh sách message vòng lặp sắp gửi
        // (mọi bước đã xong + kết quả tool), và đứng trên middleware log nên một lời gọi hỏng vẫn đã được ghi.
        IChatClient loopClient = checkpoint == null
            ? modelClient
            : new CheckpointingChatClient(modelClient, _checkpoints, checkpoint.TaskId, model.ModelId, stepsBefore);
        // Ngân sách của từng pha do PhaseStepBudget giữ, KHÔNG do MaximumIterationsPerRequest: chạm trần đó
        // thì vòng lặp của framework gỡ hết tool khỏi lời gọi cuối (PrepareOptionsForLastIteration) — một lời
        // gọi model trọn giá chỉ để nhận câu trả lời bị ép không-tool mà pha kế vứt đi (model đang làm dở vẫn
        // muốn gọi tool nên hay viết lời gọi tool thành chữ). PhaseStepBudget dừng vòng lặp ngay sau khi tool
        // của bước cuối chạy xong; trần của framework chỉ còn là lưới an toàn (xem PhaseStepBudget.Begin).
        var budget = new PhaseStepBudget(modelClient);
        // Lượt salvage phải KHÔNG có tool thật sự: ChatClientAgent GỘP tool của lượt chạy vào tool mặc định
        // của agent (Tools = [] ở run options không gỡ được gì), nên tool được gỡ ở tầng ngay dưới vòng lặp.
        var functionInvoker = new FunctionInvokingChatClient(new PhaseToolGate(loopClient, budget), _loggerFactory)
        {
            FunctionInvoker = budget.InvokeAsync
        };

        var runtimeAgent = new ChatClientAgent(functionInvoker, new ChatClientAgentOptions
        {
            Name = agent.RoleKey.GetTitle(),
            ChatOptions = new ChatOptions
            {
                Instructions = _promptBuilder.BuildNative(agent, learnedChecklist),
                Tools = aiTools
            },
            // We already composed the function-invocation pipeline above; don't let the agent wrap it again.
            UseProvidedChatClientAsIs = true
        }, _loggerFactory);

        var session = await runtimeAgent.CreateSessionAsync(cancellationToken);

        // Phase 1 — run within the expected step budget. Chạy tiếp ⇒ đầu vào là HỘI THOẠI đã khôi phục
        // (kết thúc bằng kết quả tool của bước cuối đã xong) nên model đi tiếp đúng bước bị hỏng; phần ngân
        // sách còn lại của pha là firstBudget.
        IReadOnlyList<ChatMessage> firstInput = resume != null
            ? resume.MessagesFor(model.ModelId)
            : [new ChatMessage(ChatRole.User, userMessage)];
        var (converged, text, skippedCalls) = await RunAgentPhaseAsync(runtimeAgent, session, firstInput, functionInvoker, budget, firstBudget, onToken, cancellationToken);

        // Phase 2 — auto-continue: budget ran out before the model finished. Nudge it to COMPLETE the
        // remaining work, granting turns up to the hard cap (state is already on disk) so it finishes what
        // it started without burning tokens forever. (Lượt chạy tiếp đã ở sẵn pha này thì không nhắc lần hai.)
        if (!converged && autoContinue && !resumedInAutoContinue)
        {
            var (continued, continuedText, continuedSkipped) = await RunAgentPhaseAsync(runtimeAgent, session,
                NextPhaseInput(skippedCalls,
                    "Bạn đã dùng hết ngân sách bước dự kiến nhưng công việc dường như CHƯA hoàn tất. "
                    + "Hãy tiếp tục để HOÀN THÀNH ĐẦY ĐỦ phần còn thiếu (ví dụ append nốt các phần còn lại của POC), "
                    + "tránh các bước thừa, rồi trả lời cuối (không gọi tool) khi đã xong."),
                functionInvoker, budget, hardCap - maxSteps, onToken, cancellationToken);
            converged = continued;
            text = continuedText;
            skippedCalls = continuedSkipped;
        }

        // Bàn giao cuối KHÔNG ghi vào AgentConversations: bảng đó là hội thoại người dùng ↔ BA (trang
        // Requirements hiện nó thành bubble chat, các reader transcript của BA đọc nó làm ngữ cảnh). Bàn giao
        // của agent delivery là báo cáo kỹ thuật cho đội Dev — caller giữ nó ở AgentTask.Output, đọc ở Agent
        // Dashboard.
        if (converged)
        {
            onProgress?.Invoke("final", "Agent đã hoàn tất công việc.", text);
            return text;
        }

        // Phase 3 — salvage: the hard cap is exhausted but the files the agent wrote are on disk. Give it
        // one tool-free turn (carrying the session history) to summarise what it built, turning a blank
        // failure into a partial completion. Only if that turn produces nothing is the run a real timeout.
        onProgress?.Invoke("thinking", "Đạt giới hạn bước — yêu cầu agent chốt lại kết quả đã hoàn thành.", null);
        // Lời nhắc salvage chung cho MỌI caller: định dạng bàn giao là của nhiệm vụ (vòng sửa POC đòi danh
        // sách đối chiếu, bước Implementation đòi stack + cách chạy) nên chỉ nhắc model trả đúng định dạng đó.
        var (_, salvageText, _) = await RunAgentPhaseAsync(runtimeAgent, session,
            NextPhaseInput(skippedCalls,
                "Đã đạt giới hạn số bước xử lý. KHÔNG gọi thêm bất kỳ tool nào nữa. "
                + "Hãy trả lời cuối theo ĐÚNG định dạng nhiệm vụ yêu cầu (nếu nhiệm vụ có quy định), nêu rõ những gì đã làm được "
                + "(file/tính năng, cách chạy nếu có) và phần nào còn thiếu/chưa hoàn tất."),
            functionInvoker, budget, 1, onToken, cancellationToken, toolFree: true);

        if (!string.IsNullOrWhiteSpace(salvageText))
        {
            onProgress?.Invoke("final", "Agent đã chốt kết quả (một phần) khi đạt giới hạn bước.", salvageText);
            return salvageText;
        }

        onProgress?.Invoke("final", "Dừng do đạt giới hạn số bước xử lý.", null);
        return MaxStepsReachedResult;
    }

    // Runs one agent invocation on the shared session, streaming text deltas to onToken. "Converged" means
    // the model ENDED the phase itself (answered without asking for more tools) — kể cả khi câu trả lời đó
    // rơi đúng vào bước cuối của ngân sách. Pha bị PhaseStepBudget cắt (hoặc chạm lưới an toàn của framework)
    // nghĩa là đang làm dở và cần pha kế/salvage.
    // Also returns the ids of tool calls the loop left UNEXECUTED so the next phase can answer them — see
    // NextPhaseInput. (Lưới an toàn của framework, hoặc Terminate cắt giữa một lô nhiều lời gọi.)
    private static async Task<(bool converged, string text, IReadOnlyList<string> skippedCalls)> RunAgentPhaseAsync(
        ChatClientAgent runtimeAgent, AgentSession session, IReadOnlyList<ChatMessage> messages,
        FunctionInvokingChatClient functionInvoker, PhaseStepBudget budget,
        int phaseBudget, Action<string>? onToken, CancellationToken cancellationToken, bool toolFree = false)
    {
        var safetyCap = budget.Begin(phaseBudget, toolFree);
        functionInvoker.MaximumIterationsPerRequest = safetyCap;
        var builder = new StringBuilder();
        var pendingCalls = new List<string>();
        await foreach (var update in runtimeAgent.RunStreamingAsync(messages, session, cancellationToken: cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                if (content is FunctionCallContent call)
                    pendingCalls.Add(call.CallId);
                else if (content is FunctionResultContent result)
                    pendingCalls.Remove(result.CallId);
            }

            var delta = update.Text;
            if (string.IsNullOrEmpty(delta))
                continue;

            builder.Append(delta);
            // A misbehaving sink must never break the run, so swallow anything it throws.
            if (onToken != null)
            {
                try { onToken(delta); }
                catch { }
            }
        }

        var converged = !budget.Terminated && budget.Used < safetyCap && pendingCalls.Count == 0;
        return (converged, builder.ToString(), pendingCalls);
    }

    /// <summary>
    /// Ngân sách bước của MỘT pha, đếm trên số lời gọi model thật (<see cref="ModelCallLoggingChatClient.StepCount"/>).
    /// Bước cuối của ngân sách được chạy trọn — model nhận tool như mọi bước, tool nó xin được thực thi — rồi
    /// vòng lặp dừng bằng <see cref="FunctionInvocationContext.Terminate"/> ngay sau lời gọi tool CUỐI của lô,
    /// nên không có lời gọi model nào sau đó. Hội thoại kết thúc bằng kết quả tool (hợp lệ), pha kế chỉ việc
    /// nối lời nhắc vào.
    /// </summary>
    private sealed class PhaseStepBudget(ModelCallLoggingChatClient modelClient)
    {
        // Lưới an toàn: tool không được gọi qua FunctionInvoker (model xin một tool không tồn tại) thì
        // Terminate không có cơ hội chạy — vòng lặp đi thêm một lời gọi và được cắt ở lời gọi kế. Trần của
        // framework chỉ chặn trường hợp model cứ xin tool không tồn tại mãi.
        private const int SafetyMargin = 2;

        private int _start;
        private int _limit;

        public bool Terminated { get; private set; }

        /// <summary>Pha này không quảng bá tool nào cho model (lượt salvage) — xem <see cref="PhaseToolGate"/>.</summary>
        public bool ToolFree { get; private set; }

        public int Used => modelClient.StepCount - _start;

        /// <summary>Mở một pha mới; trả trần đặt cho <see cref="FunctionInvokingChatClient.MaximumIterationsPerRequest"/>.</summary>
        public int Begin(int limit, bool toolFree = false)
        {
            _start = modelClient.StepCount;
            _limit = Math.Max(limit, 1);
            ToolFree = toolFree;
            Terminated = false;
            return _limit >= int.MaxValue - SafetyMargin ? int.MaxValue : _limit + SafetyMargin;
        }

        public async ValueTask<object?> InvokeAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
        {
            var result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
            // Chỉ cắt sau lời gọi CUỐI của lô: cắt giữa lô thì các lời gọi còn lại bị bỏ, model đã xin mà
            // không được chạy.
            if (Used >= _limit && context.FunctionCallIndex >= context.FunctionCount - 1)
            {
                context.Terminate = true;
                Terminated = true;
            }
            return result;
        }
    }

    /// <summary>Gỡ toàn bộ tool khỏi lời gọi model khi pha hiện tại là pha không-tool (<see cref="PhaseStepBudget.ToolFree"/>).</summary>
    private sealed class PhaseToolGate(IChatClient inner, PhaseStepBudget budget) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetResponseAsync(messages, Gate(options), cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetStreamingResponseAsync(messages, Gate(options), cancellationToken);

        private ChatOptions? Gate(ChatOptions? options)
        {
            if (!budget.ToolFree || options?.Tools is not { Count: > 0 })
                return options;
            var stripped = options.Clone();
            stripped.Tools = null;
            stripped.ToolMode = null;
            return stripped;
        }
    }

    // Đầu vào của pha kế: trả lời các lời gọi tool pha trước bỏ lại (nếu có) RỒI mới tới lời nhắc, để hội
    // thoại luôn hợp lệ — mọi lời gọi tool đều có kết quả đứng ngay sau.
    private static IReadOnlyList<ChatMessage> NextPhaseInput(IReadOnlyList<string> skippedCalls, string demand)
    {
        var input = new List<ChatMessage>(2);
        if (skippedCalls.Count > 0)
            input.Add(new ChatMessage(ChatRole.Tool,
                skippedCalls.Select(id => (AIContent)new FunctionResultContent(id, SkippedToolResult)).ToList()));
        input.Add(new ChatMessage(ChatRole.User, demand));
        return input;
    }
}
