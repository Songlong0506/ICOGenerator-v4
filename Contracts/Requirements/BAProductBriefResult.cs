namespace ICOGenerator.Contracts.Requirements;

// Kết quả lượt "Write Requirement" (phía user): chỉ sinh tài liệu dễ hiểu (Product Brief)
// cho người dùng xem & duyệt. Bản kỹ thuật AI Design Spec KHÔNG sinh ở đây nữa — nó được
// sinh từ Product Brief đã duyệt khi user bấm Approve (xem BAAiDesignSpecResult). Các tài liệu
// kỹ thuật nặng (BRD/SRS/FSD/UserStories) là bước 2 của Delivery Pipeline (AgentTaskType.TechnicalDocs, sau POC).
public class BAProductBriefResult
{
    public string AssistantMessage { get; set; } = "";
    public ProductBriefDto ProductBrief { get; set; } = new();

    // Van thoát "không giả định": tài liệu chỉ được chứa điều người dùng đã nói/đã chốt, nên khi model
    // soạn tài liệu phát hiện còn điểm PHẢI tự giả định mới viết được (lọt qua cổng readiness — vốn
    // fail-open khi lỗi), nó đặt cờ này kèm MỘT câu hỏi thay vì viết bừa. Service sẽ KHÔNG sinh file mà
    // đẩy câu hỏi vào khung chat (NeedsMoreInfo), giống đường cổng readiness chặn.
    public bool NeedsClarification { get; set; }

    public string ClarifyingQuestion { get; set; } = "";

    public List<string> ClarifyingSuggestions { get; set; } = new();
}
