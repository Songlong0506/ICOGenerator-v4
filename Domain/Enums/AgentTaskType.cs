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
    // Bước 2 của Delivery Pipeline (sau POC): sinh tài liệu kỹ thuật (BRD/SRS/FSD/UserStories) từ
    // Product Brief + AI Design Spec đã duyệt. Do BA chạy qua RequirementDocsService (không phải agent+
    // prompt chung), rồi rơi về cổng duyệt tuyến tính như mọi bước khác.
    TechnicalDocs = 11,
    // Sinh AI Design Spec từ Product Brief ĐÃ DUYỆT ngay sau khi Approve. Trước đây chạy đồng bộ trong
    // ApproveRequirementUseCase khiến màn hình treo chờ LLM; nay chạy NỀN trong một workflow run riêng
    // (BA, một bước) để có tiến độ live như "Write Requirement", rồi tự khởi động delivery workflow.
    AiDesignSpec = 12
}
