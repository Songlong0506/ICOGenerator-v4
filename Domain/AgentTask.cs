using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

public class AgentTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowRunId { get; set; }
    public WorkflowRun WorkflowRun { get; set; } = default!;
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;
    public Guid? AgentId { get; set; }
    public Agent? Agent { get; set; }
    public AgentTaskType Type { get; set; } = AgentTaskType.RequirementAnalysis;
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Queued;
    public string Title { get; set; } = string.Empty;
    public string Input { get; set; } = string.Empty;

    /// <summary>
    /// Nhận xét của người duyệt khi yêu cầu CHỈNH SỬA lại bước này tại cổng duyệt (null = task
    /// thường). Task chỉnh sửa giữ <see cref="Input"/> nguyên bản của bước (spec / output bước
    /// trước) — feedback nằm riêng ở đây để prompt gốc không bị trộn lẫn và các chỗ tiêu thụ
    /// Input (vd SetPocSpec) vẫn nhận đúng dữ liệu sạch.
    /// </summary>
    public string? RevisionFeedback { get; set; }

    public string? Output { get; set; }
    public string? Error { get; set; }
    public int Attempt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
