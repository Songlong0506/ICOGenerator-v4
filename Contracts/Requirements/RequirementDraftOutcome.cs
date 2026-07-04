namespace ICOGenerator.Contracts.Requirements;

// Kết quả của một lần bấm "Write Requirement".
public enum RequirementDraftOutcome
{
    // Đã soạn/cập nhật bộ tài liệu.
    Generated,

    // Còn điểm sẽ phải giả định (cổng kiểm tra chặn, hoặc bước soạn tài liệu trả needsClarification)
    // → KHÔNG sinh tài liệu, đã đẩy câu hỏi vào khung chat.
    NeedsMoreInfo
}

public static class RequirementDraftMarkers
{
    // Ghi vào AgentTask.Output khi cổng kiểm tra quyết định CHƯA sinh tài liệu, để UI hiển thị banner
    // "cần bổ sung thông tin" thay vì "đã tạo tài liệu".
    public const string NeedsMoreInfo = "NEEDS_MORE_INFO";
}
