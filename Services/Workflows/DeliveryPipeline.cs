using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Services.Workflows;

/// <summary>
/// One stage of the delivery pipeline: which role runs it, the task type it produces, a
/// human-readable title, and whether a human must approve <b>before</b> this step starts
/// (the gate sits in front of the step).
/// </summary>
public record PipelineStep(
    WorkflowStageKey Stage,
    AgentRoleKey Role,
    AgentTaskType TaskType,
    string Title,
    bool RequiresApproval = false);

/// <summary>
/// The delivery pipeline expressed as declarative data: order = hand-off order. Inserting or
/// reordering a role is a one-line change here — the orchestrator and the worker stay generic and
/// never branch on a specific stage.
/// </summary>
public static class DeliveryPipeline
{
    public static readonly IReadOnlyList<PipelineStep> Steps = new[]
    {
        new PipelineStep(
            WorkflowStageKey.ArchitectureDesign, AgentRoleKey.TechLead, AgentTaskType.ArchitectureDesign,
            "Đề xuất kiến trúc & tech stack từ requirement"),

        // Gate: a human approves the architecture before the developer starts generating the POC.
        new PipelineStep(
            WorkflowStageKey.Implementation, AgentRoleKey.Developer, AgentTaskType.Implementation,
            "Sinh POC từ AI Design Spec đã duyệt", RequiresApproval: true),

        new PipelineStep(
            WorkflowStageKey.Testing, AgentRoleKey.Tester, AgentTaskType.Testing,
            "Viết test plan & test cases, báo lỗi từ POC"),
    };

    /// <summary>The first step — where a new delivery workflow begins.</summary>
    public static PipelineStep First => Steps[0];

    /// <summary>
    /// The step that follows the one whose <see cref="PipelineStep.Stage"/> equals
    /// <paramref name="current"/>, or <c>null</c> if it was the last step (pipeline done).
    /// </summary>
    public static PipelineStep? Next(WorkflowStageKey current)
    {
        for (var i = 0; i < Steps.Count; i++)
            if (Steps[i].Stage == current)
                return i + 1 < Steps.Count ? Steps[i + 1] : null;

        return null;
    }
}
