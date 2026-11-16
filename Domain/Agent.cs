using ICOGenerator.Domain.Enums;
namespace ICOGenerator.Domain;
public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AgentRoleKey RoleKey { get; set; } = AgentRoleKey.BusinessAnalyst;
    public string Description { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public Guid AiModelId { get; set; }
    public AiModel AiModel { get; set; } = null!;

    /// <summary>
    /// Model riêng cho LƯỢT TRỌNG TÀI của buổi phỏng vấn (<c>InterviewJudge</c>: mục đích <c>BATurnJudge</c> và
    /// <c>BACoverageQuestionJudge</c>). Null = dùng <see cref="AiModel"/> như mọi lượt phụ khác. Tách ra vì lượt
    /// trọng tài chỉ PHÁN, đầu ra vài chục token, chạy ở mọi lượt chat — chỗ đầu tiên đáng thử một model rẻ hơn.
    /// Chỉ agent BA đọc trường này. FK SetNull: xoá model trọng tài thì trọng tài lùi về model chính, không chặn
    /// việc xoá như model chính.
    /// </summary>
    public Guid? JudgeAiModelId { get; set; }
    public AiModel? JudgeAiModel { get; set; }

    public string? CreatedByUsername { get; set; }
    public ICollection<AgentTool> AgentTools { get; set; } = new List<AgentTool>();
    public ICollection<AgentModelCallLog> ModelCallLogs { get; set; } = new List<AgentModelCallLog>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
