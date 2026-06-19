using System.Text.Json;
using ICOGenerator.Services.Tools.Abstractions;
using ICOGenerator.Services.Tools.Execution;

namespace ICOGenerator.Services.Tools.Registry;

public class DynamicToolInvoker
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ToolPolicyService _toolPolicyService;
    private readonly IToolExecutionLogger _toolExecutionLogger;

    public DynamicToolInvoker(ToolPolicyService toolPolicyService, IToolExecutionLogger toolExecutionLogger)
    {
        _toolPolicyService = toolPolicyService;
        _toolExecutionLogger = toolExecutionLogger;
    }

    public async Task<string> InvokeAsync(ToolRuntimeDescriptor tool, Dictionary<string, JsonElement> args)
    {
        _toolPolicyService.EnsureCanInvoke(tool, args);
        _toolExecutionLogger.LogInvocation(tool);
        var parameters = tool.Method.GetParameters();
        var values = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (!args.TryGetValue(p.Name!, out var json))
            {
                values[i] = p.HasDefaultValue ? p.DefaultValue : GetDefault(p.ParameterType);
                continue;
            }
            values[i] = JsonSerializer.Deserialize(json.GetRawText(), p.ParameterType, JsonOptions);
        }

        var result = tool.Method.Invoke(tool.Instance, values);
        string observation;
        if (result is Task<string> taskString) observation = await taskString;
        else if (result is Task task) { await task; observation = "Done"; }
        else observation = result?.ToString() ?? string.Empty;
        _toolExecutionLogger.LogResult(tool, observation);
        return observation;
    }

    private static object? GetDefault(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
}
