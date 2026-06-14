using System.Reflection;
using System.Text.Json;

namespace ICOGenerator.Services.Tools.Execution;

public static class ToolSchemaBuilder
{
    public static string BuildInputSchema(MethodInfo method)
    {
        var methodParameters = method.GetParameters();

        var properties = methodParameters.ToDictionary(
            parameter => parameter.Name ?? string.Empty,
            parameter => new
            {
                type = ToJsonType(parameter.ParameterType),
                description = parameter.Name ?? string.Empty
            });

        var required = methodParameters
            .Where(parameter => !parameter.HasDefaultValue && Nullable.GetUnderlyingType(parameter.ParameterType) == null)
            .Select(parameter => parameter.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            type = "object",
            properties,
            required
        });
    }

    private static string ToJsonType(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        if (underlyingType == typeof(string) || underlyingType == typeof(Guid)) return "string";
        if (underlyingType == typeof(bool)) return "boolean";
        if (underlyingType == typeof(int) || underlyingType == typeof(long)) return "integer";
        if (underlyingType == typeof(double) || underlyingType == typeof(decimal) || underlyingType == typeof(float)) return "number";
        return "object";
    }
}
