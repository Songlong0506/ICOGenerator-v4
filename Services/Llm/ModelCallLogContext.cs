using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Identifies one model-call site for the call log: which project/agent it belongs to, the <see cref="Purpose"/>
/// label and the <see cref="WorkflowRunId"/> the cost is attributed to. Passed into <see cref="ILlmClient"/>
/// so logging lives in one place (<see cref="ModelCallLoggingChatClient"/>) instead of being repeated at
/// every call site. Số bước ghi vào log do <see cref="ModelCallLoggingChatClient"/> tự đếm từ 1 theo instance
/// — không có call site nào cần đặt mốc riêng.
/// </summary>
public sealed record ModelCallLogContext(Guid ProjectId, Agent Agent, string Purpose, Guid? WorkflowRunId = null);
