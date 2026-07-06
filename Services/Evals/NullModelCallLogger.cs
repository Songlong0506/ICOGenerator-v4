using ICOGenerator.Domain;
using ICOGenerator.Services.Llm;

namespace ICOGenerator.Services.Evals;

/// <summary>
/// IModelCallLogger no-op cho các lời gọi eval. Lời gọi eval KHÔNG ghi vào AgentModelCallLogs vì bảng đó
/// FK cứng vào Project/Agent (eval không thuộc dự án/agent nào); token + thời lượng + lỗi của eval đã
/// được lưu đầy đủ trên EvalResult. Nhờ vậy EvalRunnerService vẫn tái dùng được middleware
/// ModelCallLoggingChatClient (deadline, trần token, dựng LlmCallResult, map lỗi) mà không chép lại logic.
/// </summary>
public sealed class NullModelCallLogger : IModelCallLogger
{
    public Task LogAsync(Guid projectId, Agent agent, LlmCallResult callResult, int step, string purpose, Guid? workflowRunId = null) =>
        Task.CompletedTask;
}
