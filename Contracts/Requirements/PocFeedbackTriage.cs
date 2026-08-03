namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả PHÂN LOẠI từng ghi chú POC (<c>Prompts/BusinessAnalyst/poc-feedback-triage.v1.md</c>): mỗi ghi
/// chú thuộc đường "chỉnh bản demo" (Developer vá HTML) hay đường "sửa tài liệu yêu cầu" (BA soạn lại rồi
/// dựng POC mới).
///
/// Đây CHỈ là bước xem trước: người dùng còn đổi được nhóm của từng ghi chú trong hộp xác nhận trước khi
/// gửi, nên model ở đây chỉ ĐỀ XUẤT — không tự khởi động đường nào.
/// </summary>
public class PocFeedbackTriageResult
{
    public List<PocFeedbackTriageVerdict> Items { get; set; } = new();
}

public class PocFeedbackTriageVerdict
{
    /// <summary>
    /// Số thứ tự 1-based ĐÚNG như danh sách gửi cho model. Không dùng Id vì model không nhìn thấy Guid —
    /// caller map ngược index → ghi chú theo đúng thứ tự đã liệt kê.
    /// </summary>
    public int Index { get; set; }

    public bool IsRequirementIssue { get; set; }

    /// <summary>Một câu ngắn nêu vì sao xếp nhóm đó — hiện trong hộp xác nhận để người dùng soát lại.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Tin nhắn (ngôi thứ nhất) soạn từ các ghi chú người dùng ĐÃ CHỌN là hiểu-sai-yêu-cầu, để đưa vào hội
/// thoại BA (<c>Prompts/BusinessAnalyst/poc-feedback-compose.v1.md</c>). Khác bước triage ở trên: đến đây
/// KHÔNG lọc bỏ gì nữa — danh sách đã là quyết định của người dùng.
/// </summary>
public class PocFeedbackComposeResult
{
    public string Message { get; set; } = string.Empty;
}
