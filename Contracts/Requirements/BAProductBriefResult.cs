namespace ICOGenerator.Contracts.Requirements;

// Kết quả lượt "Write Requirement" (phía user): chỉ sinh tài liệu dễ hiểu (Product Brief)
// cho người dùng xem & duyệt. Bản kỹ thuật AI Design Spec KHÔNG sinh ở đây nữa — nó được
// sinh từ Product Brief đã duyệt khi user bấm Approve (xem BAAiDesignSpecResult). Các tài liệu
// kỹ thuật nặng (BRD/SRS/FSD/UserStories) là bước 2 của Delivery Pipeline (AgentTaskType.TechnicalDocs, sau POC).
public class BAProductBriefResult
{
    public string AssistantMessage { get; set; } = "";
    public ProductBriefDto ProductBrief { get; set; } = new();
}
