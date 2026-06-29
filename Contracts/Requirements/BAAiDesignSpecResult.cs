namespace ICOGenerator.Contracts.Requirements;

// Kết quả bước sinh AI Design Spec từ Product Brief ĐÃ DUYỆT (chạy đồng bộ khi user bấm Approve).
// Bản kỹ thuật súc tích này là input cho Developer Agent dựng POC.
public class BAAiDesignSpecResult
{
    public string AssistantMessage { get; set; } = "";
    public AiDesignSpecDto AiDesignSpec { get; set; } = new();
}
