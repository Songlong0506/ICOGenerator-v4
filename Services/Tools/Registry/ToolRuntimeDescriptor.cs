using System.Reflection;
using ICOGenerator.Domain;

namespace ICOGenerator.Services.Tools.Registry;

public record ToolRuntimeDescriptor(
    ToolDefinition Definition,
    object Instance,
    MethodInfo Method);
