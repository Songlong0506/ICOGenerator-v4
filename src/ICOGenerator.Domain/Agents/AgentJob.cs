using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

public class AgentJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid AgentId { get; set; }

    public AgentJobStatus Status { get; set; } = AgentJobStatus.Queued;
    public string CurrentStep { get; set; } = "Waiting...";
    public string UserMessage { get; set; } = "";
    public string? Result { get; set; }
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}
