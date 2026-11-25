namespace ICOGenerator.Domain;

public class ProjectDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;

    public Guid? AgentId { get; set; }
    public Agent? Agent { get; set; }

    public string Folder { get; set; } = "01_Requirement";

    /// <summary>Tên phiên bản của bản nháp. Mọi bản khác (<c>V{n}</c>) đều là bản đã duyệt.</summary>
    public const string DraftVersion = "draft";

    /// <summary>
    /// <see cref="DraftVersion"/> = bản nháp chưa duyệt; <c>V{n}</c> = bản đã duyệt. Cột này là cờ duyệt DUY NHẤT:
    /// "draft" chỉ đổi thành "V{n}" lúc duyệt (<c>ApproveRequirementUseCase</c>), tài liệu sinh thẳng vào bản
    /// đã duyệt thì ghi luôn "V{n}". Hỏi "đã duyệt chưa" = <c>VersionName != DraftVersion</c> — viết thẳng như
    /// vậy trong truy vấn để EF dịch được xuống SQL.
    /// </summary>
    public string VersionName { get; set; } = DraftVersion;

    public string FileName { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
    public string? FilePath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Lịch sử nội dung: một revision mỗi lần Content bị ghi/ghi đè (xem ProjectDocumentRevision).</summary>
    public List<ProjectDocumentRevision> Revisions { get; set; } = new();
}
