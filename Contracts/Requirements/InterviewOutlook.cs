namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả chắt lọc "triển vọng phỏng vấn" từ hội thoại trong MỘT lời gọi (InterviewOutlookService):
///  • <see cref="OpenQuestions"/> — điểm còn MƠ HỒ / MÂU THUẪN chưa chốt: tồn đọng câu hỏi, nạp vào ngữ
///    cảnh lượt chat sau để BA hỏi cho hết ngay trong khung chat (KHÔNG có panel hiển thị).
///  • <see cref="WorkedExamples"/> — các VÍ DỤ TÍNH THỬ người dùng ĐÃ XÁC NHẬN cho quy tắc định lượng,
///    nguồn để AI Design Spec đúc thành "## 13. Worked Examples" và POC tự kiểm đối chiếu độc lập.
/// Cả hai là câu ngắn (bullet), rỗng khi hội thoại chưa có gì tương ứng.
///
/// <para>
/// PHẠM VI MÀN HÌNH từng là danh sách thứ ba của chính lời gọi này. Nó đã tách ra thành
/// <see cref="InterviewScope"/> — một lời gọi riêng chạy THƯA hơn hẳn; xem class đó cho lý do.
/// </para>
/// </summary>
public class InterviewOutlook
{
    public List<string> OpenQuestions { get; set; } = new();
    public List<string> WorkedExamples { get; set; } = new();
}
