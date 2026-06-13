using ICOGenerator.Services.Tools.Registry;

namespace ICOGenerator.Services.Tools.Execution;

public class ToolPolicyService
{
    public void EnsureCanInvoke(ToolRuntimeDescriptor tool, IReadOnlyDictionary<string, System.Text.Json.JsonElement> args)
    {
        if (!tool.Definition.IsActive)
            throw new InvalidOperationException($"Tool is inactive: {tool.Definition.Name}");

        // Unknown args passed by the model are ignored — DynamicToolInvoker only binds
        // args that match the method's parameter names, so extras are harmless.
    }
}
