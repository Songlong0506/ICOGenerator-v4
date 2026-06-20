namespace ICOGenerator.Domain;

public class AgentModelCallLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }

    // Gắn lời gọi với một WorkflowRun để tính chi phí "theo run" ở trang Usage. Nullable: chat BA tương tác
    // và các log cũ (trước khi có cột này) không thuộc run nào nên để trống.
    public Guid? WorkflowRunId { get; set; }

    public string AgentName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;

    public string RequestJson { get; set; } = string.Empty;
    public string ResponseText { get; set; } = string.Empty;
    public string? ExtractedContent { get; set; }
    public string? ErrorMessage { get; set; }

    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }

    public long DurationMs { get; set; }
    public int? HttpStatusCode { get; set; }
    public bool IsSuccess { get; set; }

    public int Step { get; set; }
    public string Purpose { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
