namespace ICOGenerator.Services.Artifacts;

public class ProjectArtifactCatalog : IProjectArtifactCatalog
{
    // Tài liệu dễ hiểu cho NGƯỜI DÙNG THƯỜNG, sinh khi user bấm "Write Requirement". Đây là thứ
    // duy nhất hiển thị ở trang Requirements.
    private static readonly ProjectArtifactDescriptor ProductBriefDoc =
        new("ProductBrief", "ProductBrief.docx", "Product Brief", true, "01_Requirement");

    // Bản kỹ thuật súc tích cho AI Developer Agent dựng POC. Sinh kèm Product Brief nhưng KHÔNG hiển
    // thị ở trang Requirements (thuộc phía Agent Dashboard) — vẫn bắt buộc có để Approve & chạy POC.
    private static readonly ProjectArtifactDescriptor AiDesignSpecDoc =
        new("AIDesignSpec", "AIDesignSpec.docx", "AI Design Spec", true, "02_Design");

    // Tài liệu kỹ thuật nặng cho team dev. KHÔNG sinh ở "Write Requirement" nữa: do team dev trigger
    // ở Agent Dashboard sau khi requirement đã được duyệt. Vì vậy không bắt buộc cho việc Approve.
    private static readonly ProjectArtifactDescriptor[] TechnicalDocs =
    [
        new("BRD", "BRD.docx", "Business Requirement Document", false, "01_Requirement"),
        new("SRS", "SRS.docx", "Software Requirement Specification", false, "01_Requirement"),
        new("FSD", "FSD.docx", "Functional Specification Document", false, "01_Requirement"),
        new("UserStories", "UserStories.docx", "User Stories", false, "01_Requirement")
    ];

    public ProjectArtifactDescriptor ProductBrief => ProductBriefDoc;
    public ProjectArtifactDescriptor AiDesignSpec => AiDesignSpecDoc;
    public IReadOnlyList<ProjectArtifactDescriptor> TechnicalDocuments => TechnicalDocs;
}
