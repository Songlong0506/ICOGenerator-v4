using ICOGenerator.Services.Registry;

namespace ICOGenerator.Services.Tools.Abstractions;

public class ToolExecutionLogger : IToolExecutionLogger
{
    private readonly ILogger<ToolExecutionLogger> _logger;

    public ToolExecutionLogger(ILogger<ToolExecutionLogger> logger)
    {
        _logger = logger;
    }

    public void LogInvocation(ToolRuntimeDescriptor tool)
    {
        _logger.LogInformation("Invoking agent tool {ToolName} via {ServiceType}.{MethodName}.", tool.Definition.Name, tool.Definition.ServiceType, tool.Definition.MethodName);
    }

    public void LogResult(ToolRuntimeDescriptor tool, string result)
    {
        var preview = result.Length > 1000 ? result[..1000] + "...[truncated]" : result;
        _logger.LogInformation("Agent tool {ToolName} completed. Result preview: {ResultPreview}", tool.Definition.Name, preview);
    }
}
