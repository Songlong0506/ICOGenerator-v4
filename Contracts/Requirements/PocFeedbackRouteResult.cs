namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả lọc ghi chú POC để gửi ngược về bước Requirement (poc-feedback-route.v1): có ghi chú nào
/// phản ánh hiểu-sai-yêu-cầu không, và nếu có thì <see cref="Message"/> là tin nhắn (ngôi thứ nhất) để
/// đưa vào hội thoại BA soạn lại tài liệu. Tất cả chỉ là thẩm mỹ ⇒ HasRequirementIssue = false.
/// </summary>
public class PocFeedbackRouteResult
{
    public bool HasRequirementIssue { get; set; }
    public string Message { get; set; } = string.Empty;
}
