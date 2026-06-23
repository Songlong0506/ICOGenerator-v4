using System.Reflection;
using System.Text.Json;

namespace ICOGenerator.Services.Tools.Registry;

/// <summary>
/// Checks that a model-requested tool call actually carries the arguments the target method requires.
///
/// On the streaming native tool-calling path a call can arrive with its arguments missing: the
/// streamed argument fragments weren't reassembled, or the arguments JSON was cut off by the token
/// limit (finish_reason=length). <see cref="DynamicToolInvoker"/> then binds the absent parameters to
/// null/default, which silently corrupts state — e.g. SetPocContent invoked with no <c>content</c>
/// wipes the POC body yet still returns "POC content updated", so the run reports success with an
/// empty result. Detecting the missing arguments lets the caller refuse the call and ask the model to
/// resend, instead of running it empty.
/// </summary>
public static class ToolArgumentValidator
{
    /// <summary>
    /// Returns the names of the method's required parameters (no default value) that are absent from
    /// <paramref name="args"/>, or present but JSON null. Optional parameters and a trailing
    /// <see cref="CancellationToken"/> (never supplied by the model) are ignored. An empty result
    /// means every required argument is present, so the call is safe to bind and invoke.
    /// </summary>
    public static IReadOnlyList<string> FindMissingRequiredArguments(
        MethodInfo method, IReadOnlyDictionary<string, JsonElement> args)
    {
        var missing = new List<string>();
        foreach (var p in method.GetParameters())
        {
            if (p.HasDefaultValue || p.ParameterType == typeof(CancellationToken) || p.Name is not { } name)
                continue;

            if (!args.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                missing.Add(name);
        }

        return missing;
    }
}
