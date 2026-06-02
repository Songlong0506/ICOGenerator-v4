using ICOGenerator.Services.Registry;

namespace ICOGenerator.Services.Tools.Abstractions;

public class ToolPolicyService
{
    public void EnsureCanInvoke(ToolRuntimeDescriptor tool, IReadOnlyDictionary<string, System.Text.Json.JsonElement> args)
    {
        if (!tool.Definition.IsActive)
            throw new InvalidOperationException($"Tool is inactive: {tool.Definition.Name}");

        var allowedParameterNames = tool.Method.GetParameters().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownArgs = args.Keys.Where(x => !allowedParameterNames.Contains(x)).ToList();
        if (unknownArgs.Count > 0)
            throw new InvalidOperationException($"Unknown tool argument(s): {string.Join(", ", unknownArgs)}");
    }
}
