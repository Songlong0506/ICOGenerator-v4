namespace ICOGenerator.Contracts.Requirements;

// Kết quả lượt "Write Requirement" (phía user): chỉ sinh tài liệu dễ hiểu (Product Brief)
// cho người dùng + AI Design Spec (bản kỹ thuật, ẩn khỏi trang Requirements) để agent dựng POC.
// Các tài liệu kỹ thuật nặng (BRD/SRS/FSD/UserStories) KHÔNG sinh ở đây — chúng là bước 2 của
// Delivery Pipeline (AgentTaskType.TechnicalDocs, sau POC).
public class BAProductBriefResult
{
    public string AssistantMessage { get; set; } = "";
    public ProductBriefDto ProductBrief { get; set; } = new();
    public AiDesignSpecDto AiDesignSpec { get; set; } = new();
}
