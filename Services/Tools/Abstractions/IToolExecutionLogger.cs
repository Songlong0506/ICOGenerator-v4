using ICOGenerator.Services.Registry;

namespace ICOGenerator.Services.Tools.Abstractions;

public interface IToolExecutionLogger
{
    void LogInvocation(ToolRuntimeDescriptor tool);
    void LogResult(ToolRuntimeDescriptor tool, string result);
}
