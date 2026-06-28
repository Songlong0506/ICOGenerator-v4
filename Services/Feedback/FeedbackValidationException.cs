namespace ICOGenerator.Services.Feedback;

/// <summary>
/// Lỗi validate phản hồi / file đính kèm (định dạng, kích thước, số lượng) — controller bắt để báo người
/// dùng bằng thông báo thân thiện thay vì để văng thành lỗi 500.
/// </summary>
public class FeedbackValidationException : Exception
{
    public FeedbackValidationException(string message) : base(message) { }
}
