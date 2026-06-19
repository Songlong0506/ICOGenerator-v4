namespace ICOGenerator.Domain;

public class AgentConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;

    public Guid AgentId { get; set; }
    public Agent Agent { get; set; } = default!;

    public string Role { get; set; } = "assistant"; // user / assistant

    public string Message { get; set; } = string.Empty;

    // JSON array (chuỗi) các đáp án gợi ý cho lượt hỏi của BA, để UI render thành "chip" bấm chọn
    // (giống plan mode). Null/không có nghĩa là lượt này không kèm gợi ý. Chỉ là phụ trợ cho UI —
    // KHÔNG đưa vào ngữ cảnh gửi lại cho LLM (Message vẫn giữ phần text thuần để giữ context sạch).
    public string? Suggestions { get; set; }

    public int TokenUsed { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
