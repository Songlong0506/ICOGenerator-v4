using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Application.Feedback;

/// <summary>Dữ liệu form gửi phản hồi (không gồm file — file nhận riêng qua IFormFile ở controller).</summary>
public class SubmitFeedbackVm
{
    public FeedbackType Type { get; set; } = FeedbackType.Bug;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
