namespace ICOGenerator.Domain.Enums;

public enum AgentTaskType
{
    RequirementAnalysis = 2,
    ArchitectureDesign = 3,
    Implementation = 4,
    CodeReview = 5,
    Testing = 6,
    BugFix = 7,
    PocPreview = 9,
    PullRequest = 10,
    // Team dev trigger ở Agent Dashboard: sinh tài liệu kỹ thuật (BRD/SRS/FSD/UserStories) từ
    // Product Brief + AI Design Spec đã duyệt. Là workflow một-bước (như RequirementAnalysis),
    // không thuộc Delivery Pipeline.
    TechnicalDocs = 11
}
