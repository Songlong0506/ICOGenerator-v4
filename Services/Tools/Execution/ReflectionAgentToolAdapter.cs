using System.Text.Json;
using ICOGenerator.Services.Tools.Abstractions;
using ICOGenerator.Services.Tools.Registry;

namespace ICOGenerator.Services.Tools.Execution;

public class ReflectionAgentToolAdapter : IAgentTool<Dictionary<string, JsonElement>, string>
{
    private readonly ToolRuntimeDescriptor _descriptor;
    private readonly DynamicToolInvoker _invoker;

    public ReflectionAgentToolAdapter(ToolRuntimeDescriptor descriptor, DynamicToolInvoker invoker)
    {
        _descriptor = descriptor;
        _invoker = invoker;
        Metadata = new ToolMetadata(
            descriptor.Definition.Name,
            descriptor.Definition.Description,
            ToolSchemaBuilder.BuildInputSchema(descriptor.Method));
    }

    public ToolMetadata Metadata { get; }

    public async Task<ToolResult<string>> ExecuteAsync(Dictionary<string, JsonElement> input, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            var output = await _invoker.InvokeAsync(_descriptor, input);
            return new ToolResult<string>(true, output);
        }
        catch (Exception ex)
        {
            return new ToolResult<string>(false, null, ex.Message);
        }
    }
}
