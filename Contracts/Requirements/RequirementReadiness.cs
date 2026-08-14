namespace ICOGenerator.Contracts.Requirements;

// Kết quả cổng readiness TẤT ĐỊNH (RequirementReadinessGate.Evaluate — suy từ bản đồ bao phủ, không gọi
// LLM). Khi Ready=false, Message được đẩy vào khung chat như một lượt BA để người dùng bổ sung, và lời
// mời "Write Requirement"/bước sinh tài liệu bị chặn lại (fail-closed).
public class RequirementReadiness
{
    public bool Ready { get; set; }

    public string Message { get; set; } = "";

    /// <summary>
    /// Lượt chặn của cổng luôn là một câu MỞ: nó xin một mẩu thông tin còn thiếu, và không có bộ chip nào
    /// dựng sẵn tất định được (chip phải là đáp án TRỌN VẸN cho đúng câu đang hỏi — thứ chỉ BA viết ra
    /// được). Cờ này đi tiếp ra <c>BAChatTurnResult.OpenEnded</c> để khung chat đổi placeholder thành lời
    /// mời kể. Không có nó, lượt gate lên màn hình vừa không có chip vừa không mời gõ — đúng thứ
    /// <c>requirement-chat.v4.md</c> gọi là "một lượt hỏi thiếu chỗ trả lời".
    /// </summary>
    public bool OpenEnded { get; set; }
}
