namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả chắt lọc "triển vọng phỏng vấn" từ hội thoại trong MỘT lời gọi (InterviewOutlookService):
///  • <see cref="OpenQuestions"/> — điểm còn MƠ HỒ / MÂU THUẪN chưa chốt: tồn đọng câu hỏi, nạp vào ngữ
///    cảnh lượt chat sau để BA hỏi cho hết ngay trong khung chat (KHÔNG có panel hiển thị).
///  • <see cref="PlannedScope"/> — các MÀN HÌNH / TÍNH NĂNG dự kiến, dựng dần theo hội thoại: nạp làm ngữ
///    cảnh cho bộ soát mâu thuẫn (KHÔNG có panel hiển thị).
///  • <see cref="WorkedExamples"/> — các VÍ DỤ TÍNH THỬ người dùng ĐÃ XÁC NHẬN cho quy tắc định lượng,
///    nguồn để AI Design Spec đúc thành "## 13. Worked Examples" và POC tự kiểm đối chiếu độc lập.
/// Ba danh sách đều là câu ngắn (bullet), rỗng khi hội thoại chưa có gì tương ứng.
/// </summary>
public class InterviewOutlook
{
    public List<string> OpenQuestions { get; set; } = new();
    public List<string> PlannedScope { get; set; } = new();
    public List<string> WorkedExamples { get; set; } = new();
}
