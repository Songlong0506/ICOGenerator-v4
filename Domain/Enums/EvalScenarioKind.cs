namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Kiểu tình huống trong golden set.
/// </summary>
public enum EvalScenarioKind
{
    /// <summary>
    /// MỘT lượt: gửi một đầu vào cho prompt rồi chấm câu trả lời. Đo được cách BA phản ứng trong một
    /// lượt, KHÔNG đo được thứ quyết định chất lượng yêu cầu — cả cuộc phỏng vấn có đi tới đích không.
    /// </summary>
    Prompt = 0,

    /// <summary>
    /// PHỎNG VẤN MÔ PHỎNG: một model đóng vai người dùng nghiệp vụ theo một hồ sơ persona, BA hỏi và
    /// persona trả lời qua nhiều lượt cho tới khi BA mời bấm "Write Requirement" (hoặc chạm trần lượt).
    /// Đây là tầng eval trả lời được câu hỏi mà tầng một-lượt không chạm tới: sửa prompt hôm nay làm
    /// cuộc phỏng vấn ngắn đi hay dài ra, có còn tới đích không, có vi phạm quy tắc "mỗi lượt một câu
    /// hỏi" không.
    /// </summary>
    Interview = 1
}
