namespace ICOGenerator.Contracts.Requirements;

// Kết quả lượt "Write Requirement" (phía user): chỉ sinh tài liệu dễ hiểu (Product Brief)
// cho người dùng + AI Design Spec (bản kỹ thuật, ẩn khỏi trang Requirements) để agent dựng POC.
// Các tài liệu kỹ thuật nặng (BRD/SRS/FSD/UserStories) KHÔNG sinh ở đây — chúng do team dev
// trigger ở Agent Dashboard (xem BATechnicalDocs path).
public class BAProductBriefResult
{
    public string AssistantMessage { get; set; } = "";
    public ProductBriefDto ProductBrief { get; set; } = new();
    public AiDesignSpecDto AiDesignSpec { get; set; } = new();
}
