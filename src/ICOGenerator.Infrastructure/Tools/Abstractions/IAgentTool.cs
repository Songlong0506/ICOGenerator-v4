namespace ICOGenerator.Services.Tools.Abstractions;

public interface IAgentTool<TInput, TOutput>
{
    ToolMetadata Metadata { get; }
    Task<ToolResult<TOutput>> ExecuteAsync(TInput input, ToolExecutionContext context, CancellationToken cancellationToken);
}
