namespace ICOGenerator.Domain;

/// <summary>
/// Một góp ý của reviewer trên Product Brief của project. Neo vào một ĐOẠN TRÍCH văn bản
/// (<see cref="AnchorText"/>) thay vì vị trí ký tự — brief được sinh lại nhiều lần nên offset sẽ trôi,
/// còn đoạn trích vẫn đọc hiểu được kể cả khi nội dung đã đổi. Góp ý sống theo PROJECT (không FK vào
/// ProjectDocument) vì nó nhắm tới "Product Brief của dự án" xuyên suốt các lần sinh lại bản draft.
/// Vòng đời: tạo → (chủ project chèn các góp ý mở vào chat BA để sửa brief) → resolve.
/// </summary>
public class BriefComment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public string AuthorUsername { get; set; } = string.Empty;

    /// <summary>Đoạn trích trong brief mà góp ý nhắm tới (null = góp ý chung cho cả tài liệu).</summary>
    public string? AnchorText { get; set; }

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Null = còn mở. Đã resolve thì giữ lại làm lịch sử, không xóa.</summary>
    public DateTime? ResolvedAt { get; set; }

    public string? ResolvedByUsername { get; set; }
}
