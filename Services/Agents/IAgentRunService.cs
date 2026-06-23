namespace ICOGenerator.Services.Agents;

/// <summary>
/// Seam cho phép hoán đổi vòng lặp chạy agent sau một feature-flag. Mặc định là <see cref="AgentRunService"/>
/// (vòng lặp tự xây). Khi build với <c>-p:EnableMafSpike=true</c> và bật cấu hình
/// <c>Llm:AgentRuntime:UseAgentFramework</c>, đường native tool-calling được phục vụ bởi
/// <c>ChatClientAgentRunService</c> (Microsoft Agent Framework). Giữ nguyên chữ ký của
/// <see cref="AgentRunService.RunAsync"/> để mọi caller (hiện chỉ có <c>AgentTaskWorker</c>) không phải đổi.
/// </summary>
public interface IAgentRunService
{
    Task<string> RunAsync(Guid projectId, Guid agentId, string userMessage, int maxSteps = 6,
        Action<string, string, string?>? onProgress = null, Func<string, string, bool>? stopWhen = null,
        Action<string>? onToken = null, Guid? workflowRunId = null, CancellationToken cancellationToken = default);
}
