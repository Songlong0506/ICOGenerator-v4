using ICOGenerator.Domain;
using System.Reflection;

namespace ICOGenerator.Services.Registry;

public record ToolRuntimeDescriptor(
    ToolDefinition Definition,
    object Instance,
    MethodInfo Method);
