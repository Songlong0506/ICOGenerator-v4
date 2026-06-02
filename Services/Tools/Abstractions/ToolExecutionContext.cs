namespace ICOGenerator.Services.Tools.Abstractions;

public record ToolExecutionContext(Guid? ProjectId, Guid? WorkflowRunId, Guid? AgentTaskId, Guid? AgentId, string WorkspacePath);
