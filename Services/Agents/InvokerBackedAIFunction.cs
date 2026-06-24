using System.Reflection;
using System.Text.Json;
using ICOGenerator.Services.Tools.Abstractions;
using ICOGenerator.Services.Tools.Execution;
using ICOGenerator.Services.Tools.Registry;
using Microsoft.Extensions.AI;

namespace ICOGenerator.Services.Agents;

/// <summary>
/// Wraps a workspace tool for the agent framework's function-invocation loop. Name, JSON schema, argument
/// binding and the method invocation itself all come from the wrapped <see cref="AIFunction"/> (built by
/// <see cref="AIFunctionFactory"/> from the method signature) — this type no longer re-binds arguments or
/// invokes by reflection. It only layers the app's cross-cutting concerns over that single invocation:
/// progress reporting, the per-agent <see cref="ToolPolicyService">policy</see> check,
/// <see cref="IToolExecutionLogger">execution logging</see>, and the truncation guard below.
///
/// Truncation guard: a streamed tool call can arrive with its required arguments missing (fragments not
/// reassembled, or the arguments JSON cut off by the token limit). Letting the binder fill the absent
/// parameters with null/default silently corrupts state — e.g. SetPocContent with no <c>content</c> wipes
/// the POC body yet returns success. Such a call is refused (not run) and an error observation is returned
/// so the model re-issues the call with complete arguments.
/// </summary>
public sealed class InvokerBackedAIFunction : DelegatingAIFunction
{
    private readonly ToolRuntimeDescriptor _descriptor;
    private readonly ToolPolicyService _policy;
    private readonly IToolExecutionLogger _logger;
    private readonly Action<string, string, string?>? _onProgress;

    public InvokerBackedAIFunction(
        AIFunction inner, ToolRuntimeDescriptor descriptor, ToolPolicyService policy,
        IToolExecutionLogger logger, Action<string, string, string?>? onProgress) : base(inner)
    {
        _descriptor = descriptor;
        _policy = policy;
        _logger = logger;
        _onProgress = onProgress;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var args = ToJsonArgs(arguments);
        _onProgress?.Invoke("tool", $"Đang dùng tool: {Name}", DescribeArgs(args));

        // Refuse a call whose required arguments didn't arrive, feeding the problem back so the model
        // re-issues it instead of running an empty, possibly destructive call.
        if (ToolArgumentValidator.FindMissingRequiredArguments(_descriptor.Method, args) is { Count: > 0 } missing)
        {
            var error = DescribeMissingArguments(Name, missing);
            _onProgress?.Invoke("error", $"Tool {Name} thiếu đối số bắt buộc — yêu cầu agent gọi lại.", error);
            return error;
        }

        string observation;
        try
        {
            // Policy + logging are this wrapper's job; binding + invocation are the inner AIFunction's
            // (it built the schema from the same signature, so the round-trip stays consistent).
            _policy.EnsureCanInvoke(_descriptor);
            _logger.LogInvocation(_descriptor);
            observation = ToObservation(await base.InvokeCoreAsync(arguments, cancellationToken));
            _logger.LogResult(_descriptor, observation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Feed a recoverable tool failure back as an observation so the model can correct itself
            // instead of aborting the run; unwrap the reflection wrapper for a useful message.
            var real = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            observation = $"ERROR: {real.Message}";
        }

        _onProgress?.Invoke("observation", $"Đã nhận kết quả từ {Name}", observation);
        return observation;
    }

    // The tool result the model sees is a plain string for every registered tool (each returns
    // Task<string> or string). Mirror the old invoker's contract for the rare non-string return: a
    // non-generic Task tool reported "Done"; anything else that comes back null → empty.
    private string ToObservation(object? result)
    {
        if (result is string s) return s;
        if (result is not null) return result.ToString() ?? string.Empty;
        return _descriptor.Method.ReturnType == typeof(Task) ? "Done" : string.Empty;
    }

    // Converts the framework's tool-call arguments (object? values, usually already JsonElement) into the
    // JsonElement map the validator and progress display read.
    private static Dictionary<string, JsonElement> ToJsonArgs(AIFunctionArguments arguments)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in arguments)
            result[key] = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value);
        return result;
    }

    private static string DescribeMissingArguments(string toolName, IReadOnlyList<string> missing)
    {
        var names = string.Join(", ", missing);
        return $"ERROR: the call to '{toolName}' is missing required argument(s) [{names}] "
            + "(the arguments did not arrive in full — e.g. the response was cut off by the token limit). "
            + "The tool was NOT run. Re-issue the call with COMPLETE and concise arguments so it fits the token limit.";
    }

    private static string? DescribeArgs(Dictionary<string, JsonElement> args)
    {
        if (args.Count == 0)
            return null;

        var parts = args.Select(kv =>
        {
            var value = kv.Value.ToString();
            if (value.Length > 80) value = value[..80] + "…";
            return $"{kv.Key}: {value}";
        });

        return string.Join("\n", parts);
    }
}
