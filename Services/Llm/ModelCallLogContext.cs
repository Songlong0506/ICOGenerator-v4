using ICOGenerator.Domain;

namespace ICOGenerator.Services.Llm;

/// <summary>
/// Identifies one model-call site for the call log: which project/agent it belongs to, the <see cref="Purpose"/>
/// label and the <see cref="WorkflowRunId"/> the cost is attributed to. <see cref="FirstStep"/> is the step
/// number recorded for the first call: the prompt-based fallback loop drives the step itself (one call per
/// context), whereas the agent's native path reuses a single client across many calls and lets it
/// auto-increment from here. Passed into <see cref="ILlmClient"/> so logging lives in one place
/// (<see cref="ModelCallLoggingChatClient"/>) instead of being repeated at every call site.
/// </summary>
public sealed record ModelCallLogContext(Guid ProjectId, Agent Agent, string Purpose, Guid? WorkflowRunId = null, int FirstStep = 1);
