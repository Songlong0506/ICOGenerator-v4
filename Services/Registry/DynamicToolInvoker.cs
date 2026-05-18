using System.Text.Json;

namespace ICOGenerator.Services.Registry;

public class DynamicToolInvoker
{
    public async Task<string> InvokeAsync(ToolRuntimeDescriptor tool, Dictionary<string, JsonElement> args)
    {
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
            values[i] = JsonSerializer.Deserialize(json.GetRawText(), p.ParameterType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        var result = tool.Method.Invoke(tool.Instance, values);
        if (result is Task<string> taskString) return await taskString;
        if (result is Task task) { await task; return "Done"; }
        return result?.ToString() ?? string.Empty;
    }

    private static object? GetDefault(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
}
