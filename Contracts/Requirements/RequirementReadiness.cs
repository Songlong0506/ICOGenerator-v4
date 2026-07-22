namespace ICOGenerator.Contracts.Requirements;

// Kết quả cổng readiness TẤT ĐỊNH (RequirementReadinessGate.Evaluate — suy từ bản đồ bao phủ, không gọi
// LLM). Khi Ready=false, Message (+ Suggestions nếu có) được đẩy vào khung chat như một lượt BA để người
// dùng bổ sung, và lời mời "Write Requirement"/bước sinh tài liệu bị chặn lại (fail-closed).
public class RequirementReadiness
{
    public bool Ready { get; set; }

    public string Message { get; set; } = "";

    public List<string> Suggestions { get; set; } = new();
}
