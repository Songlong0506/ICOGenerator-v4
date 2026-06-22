namespace ICOGenerator.Services.Workflows.Maf;

/// <summary>
/// Config switch selecting the delivery-pipeline engine. Off by default: the proven DB-task
/// <see cref="AgentTaskWorker"/> stays the default and the opt-in Microsoft Agent Framework engine only
/// drives runs when explicitly enabled — standard practice for swapping a core subsystem.
///
///   "Workflows": { "UseMafEngine": true }
///
/// Tiny and config-bound so the choice is testable in isolation (mirrors NativeToolCallingPolicy).
/// </summary>
public sealed class MafWorkflowPolicy
{
    public bool UseMafEngine { get; }

    public MafWorkflowPolicy(IConfiguration configuration)
        : this(configuration.GetValue("Workflows:UseMafEngine", false))
    {
    }

    public MafWorkflowPolicy(bool useMafEngine) => UseMafEngine = useMafEngine;
}
