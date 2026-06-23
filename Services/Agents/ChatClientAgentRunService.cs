#if USE_MAF_SPIKE
using ICOGenerator.Data;
using ICOGenerator.Domain;
using ICOGenerator.Services.Artifacts;
using ICOGenerator.Services.Llm;
using ICOGenerator.Services.Logging;
using ICOGenerator.Services.Tools;
using ICOGenerator.Services.Tools.Registry;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Agents;

/// <summary>
/// SPIKE — bản thay thế đường NATIVE tool-calling của <see cref="AgentRunService"/> bằng Microsoft Agent
/// Framework (MAF 1.0, <c>ChatClientAgent</c>). Chỉ được biên dịch khi định nghĩa hằng build
/// <c>USE_MAF_SPIKE</c> và chỉ được chọn khi <c>Llm:AgentRuntime:UseAgentFramework = true</c>.
///
/// Mục tiêu: ĐO thực tế MAF bỏ được bao nhiêu code vòng lặp và hành vi nào phải "re-home". Toàn bộ vòng
/// for đếm bước + tự nối ChatMessage + tự dispatch tool + tự ghép FunctionResultContent trong
/// <c>AgentRunService.RunWithNativeToolsAsync</c> (~150 dòng) co lại còn phần "── MAF loop ──" bên dưới.
///
/// Model KHÔNG hỗ trợ native tool-calling vẫn đi qua vòng lặp cũ (delegate sang <see cref="AgentRunService"/>)
/// — MAF giả định native function-calling nên đường fallback prompt-based KHÔNG bị thay.
///
/// CHƯA build được trong sandbox này (không có .NET SDK) nên một số TÊN API của MAF cần đối chiếu lại với
/// package khi restore (đã chú thích ở các điểm chưa chắc). Phần scaffolding (interface, flag, delegate
/// fallback, middleware logging) là C# chuẩn, không phụ thuộc MAF.
/// </summary>
public class ChatClientAgentRunService : IAgentRunService
{
    private readonly AppDbContext _db;
    private readonly IToolRegistry _toolRegistry;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly AgentPromptBuilder _promptBuilder;
    private readonly IModelCallLogger _modelCallLogger;
    private readonly WorkspaceTools _workspaceTools;
    private readonly NativeToolCallingPolicy _nativeToolPolicy;
    private readonly AgentRunService _fallbackLoop;

    public ChatClientAgentRunService(AppDbContext db, IToolRegistry toolRegistry, IChatClientFactory chatClientFactory,
        AgentPromptBuilder promptBuilder, IModelCallLogger modelCallLogger, WorkspaceTools workspaceTools,
        NativeToolCallingPolicy nativeToolPolicy, AgentRunService fallbackLoop)
    {
        _db = db;
        _toolRegistry = toolRegistry;
        _chatClientFactory = chatClientFactory;
        _promptBuilder = promptBuilder;
        _modelCallLogger = modelCallLogger;
        _workspaceTools = workspaceTools;
        _nativeToolPolicy = nativeToolPolicy;
        _fallbackLoop = fallbackLoop;
    }

    public async Task<string> RunAsync(Guid projectId, Guid agentId, string userMessage, int maxSteps = 6,
        Action<string, string, string?>? onProgress = null, Func<string, string, bool>? stopWhen = null,
        Action<string>? onToken = null, Guid? workflowRunId = null, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FindAsync([projectId], cancellationToken) ?? throw new InvalidOperationException("Project not found.");
        var agent = await _db.Agents.Include(x => x.AiModel).FirstAsync(x => x.Id == agentId, cancellationToken);
        if (agent.AiModel == null) throw new InvalidOperationException("Agent model is not configured.");

        // MAF chỉ thay đường NATIVE. Model bị đánh dấu không-native → trả nguyên về vòng lặp cũ (đường này
        // sẽ tự route sang fallback prompt-based vì policy như nhau). Đây là lý do giữ AgentRunService.
        if (!_nativeToolPolicy.UseNativeTools(agent.AiModel))
            return await _fallbackLoop.RunAsync(projectId, agentId, userMessage, maxSteps, onProgress, stopWhen, onToken, workflowRunId, cancellationToken);

        _workspaceTools.SetWorkspace(WorkspacePathResolver.GetWorkspaceFolder(project.Id, project.Name));
        _workspaceTools.SetRunCancellation(cancellationToken);

        // Tool set dựng GIỐNG HỆT đường cũ: AIFunctionFactory.Create(method, instance). Khác biệt duy nhất
        // là MAF tự GỌI các AIFunction này thay vì DynamicToolInvoker.
        // ⚠️ GAP: gọi thẳng AIFunction bỏ qua DynamicToolInvoker → mất per-tool execution log
        //    (IToolExecutionLogger) + check ToolPolicyService.IsActive. (An toàn lệnh KHÔNG mất: nó nằm
        //    trong CommandTools.) Muốn giữ: bọc mỗi tool bằng AIFunctionFactory.Create(async args =>
        //    await _invoker.InvokeAsync(descriptor, args), name, description) — xem SPIKE-maf-agent.md.
        var tools = await _toolRegistry.GetToolsForAgentAsync(agentId);
        var aiTools = tools.Select(t => (AITool)AIFunctionFactory.Create(t.Method, t.Instance)).ToList();

        // Trần cứng số vòng auto tool-call, mirror AgentRunService.AutoContinueFactor (= maxSteps * 3).
        var hardCap = maxSteps * 3;

        // step: đếm số lời gọi model THẬT (mỗi vòng FunctionInvoking = 1 call) cho call-log, giống cũ.
        var step = 0;

        var pipeline = _chatClientFactory.Create(agent.AiModel)
            .AsBuilder()
            // FunctionInvoking phải ở NGOÀI logging để mỗi vòng lặp tool-call gọi xuống logging→raw client,
            // nhờ vậy CallLoggingChatClient ghi được TỪNG lời gọi model. (Nếu build báo sai thứ tự thì đảo
            // hai dòng dưới — semantics builder cần xác nhận khi restore package.)
            .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = hardCap)
            .Use(inner => new CallLoggingChatClient(inner, async call =>
            {
                // Bù các trường model mà middleware không biết (nó chỉ thấy IChatClient, không thấy AiModel).
                call.ModelName = agent.AiModel.Name;
                call.ModelId = agent.AiModel.ModelId;
                call.Endpoint = agent.AiModel.Endpoint;
                await _modelCallLogger.LogAsync(projectId, agent, call, ++step, "AgentRun(MAF)", workflowRunId);
            }))
            .Build();

        // ── MAF loop ── Cả vòng lặp ReAct gói gọn ở đây. ChatClientAgent tự chạy think→tool→observe.
        AIAgent runner = new ChatClientAgent(pipeline, new ChatClientAgentOptions
        {
            Name = agent.Name,
            Instructions = _promptBuilder.BuildNative(agent),
            ChatOptions = new ChatOptions
            {
                Temperature = (float)agent.Temperature,
                Tools = aiTools,
                ToolMode = ChatToolMode.Auto
                // ⚠️ GAP: chưa re-home ResolveMaxTokens (clamp theo ContextWindow) của LlmClient — model
                //    context nhỏ có thể tràn. Thêm MaxOutputTokens nếu cần.
            }
        });

        var thread = runner.GetNewThread();
        onProgress?.Invoke("thinking", $"Agent {agent.Name} đang suy nghĩ… (MAF, trần {hardCap} vòng)", null);

        var updates = new List<AgentRunResponseUpdate>();
        // ⚠️ GAP: per-call deadline. OpenAIChatClientFactory đặt NetworkTimeout=Infinite vì LlmClient vốn
        //    giữ deadline; đường MAF bỏ qua LlmClient nên KHÔNG còn timeout mỗi call — stream treo sẽ treo
        //    cả run. Re-home bằng một middleware timeout (~10 dòng) nếu đưa vào production.
        await foreach (var update in runner.RunStreamingAsync(userMessage, thread, cancellationToken: cancellationToken))
        {
            updates.Add(update);

            var delta = update.Text;
            if (!string.IsNullOrEmpty(delta) && onToken != null)
            {
                try { onToken(delta); }
                catch { /* ignore UI streaming failures */ }
            }

            // Best-effort: map content MAF → các "kind" sự kiện UI cũ. Kém chi tiết hơn đường tay (không có
            // preview đối số, không có thông điệp "thiếu đối số bắt buộc").
            foreach (var content in update.Contents)
            {
                if (content is FunctionCallContent fc)
                    onProgress?.Invoke("tool", $"Đang dùng tool: {fc.Name}", null);
                else if (content is FunctionResultContent fr)
                    onProgress?.Invoke("observation", "Đã nhận kết quả từ tool", fr.Result?.ToString());
            }
        }

        var finalText = updates.ToAgentRunResponse().Text ?? string.Empty;
        onProgress?.Invoke("final", "Agent đã hoàn tất công việc.", finalText);

        // ⚠️ GAP: KHÔNG có salvage/auto-continue. AgentRunService cho 1 lượt "chốt kết quả một phần" khi cạn
        //    budget; MAF dừng ở MaximumIterationsPerRequest. Chạm trần ở đây trả về text rỗng/dở thay vì
        //    MaxStepsReachedResult → AgentTaskWorker có thể đánh Completed nhầm. Re-home: nếu finalText rỗng
        //    sau khi cạn vòng thì chạy thêm runner.RunAsync(thread) KHÔNG tool để tóm tắt (xem SPIKE md).

        _db.AgentConversations.Add(new AgentConversation
        {
            ProjectId = projectId,
            AgentId = agentId,
            Role = "assistant",
            Message = finalText,
            TokenUsed = TokenEstimator.Estimate(finalText)
        });
        await _db.SaveChangesAsync(cancellationToken);

        return finalText;
    }
}
#endif
