namespace ICOGenerator.Domain;

/// <summary>
/// One persisted Microsoft Agent Framework workflow checkpoint. The MAF delivery-pipeline engine
/// (opt-in via <c>Workflows:UseMafEngine</c>) checkpoints at every superstep through
/// <c>EfWorkflowCheckpointStore</c>; these rows are what make a run durable — survivable across app
/// restarts and resumable after a human-approval gate. <see cref="SessionId"/> is the owning
/// <see cref="WorkflowRun"/> id (as string), so a run's checkpoints can be listed and the latest
/// resumed.
/// </summary>
public class WorkflowCheckpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>MAF session id == the owning WorkflowRun id (string form).</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>MAF checkpoint id, unique within a session.</summary>
    public string CheckpointId { get; set; } = string.Empty;

    /// <summary>The parent checkpoint id in the checkpoint tree, or null for the first.</summary>
    public string? ParentCheckpointId { get; set; }

    /// <summary>The serialized checkpoint payload (a JsonElement, stored as text).</summary>
    public string Data { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
