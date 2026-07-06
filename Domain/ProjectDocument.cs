namespace ICOGenerator.Domain;

public class ProjectDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;

    public Guid? AgentId { get; set; }
    public Agent? Agent { get; set; }

    public string Folder { get; set; } = "01_Requirement";

    public string VersionName { get; set; } = "draft";

    public bool IsApproved { get; set; } = false;

    public string FileName { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public int TokenUsed { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Lịch sử nội dung: một revision mỗi lần Content bị ghi/ghi đè (xem ProjectDocumentRevision).</summary>
    public List<ProjectDocumentRevision> Revisions { get; set; } = new();
}
