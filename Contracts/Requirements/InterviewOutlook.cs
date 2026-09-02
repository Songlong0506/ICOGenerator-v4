namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả chắt lọc "triển vọng phỏng vấn" từ hội thoại trong MỘT lời gọi (InterviewOutlookService):
///  • <see cref="OpenQuestions"/> — điểm còn MƠ HỒ / MÂU THUẪN chưa chốt: tồn đọng câu hỏi, nạp vào ngữ
///    cảnh lượt chat sau để BA hỏi cho hết ngay trong khung chat (KHÔNG có panel hiển thị). Mỗi mục mang
///    NHÓM riêng thành trường (<see cref="OpenQuestionEntry.Group"/>) chứ không phải một thẻ gõ tay ở đầu
///    chuỗi — xem <see cref="OpenQuestionDocument"/> cho cái giá của khuôn cũ.
///  • <see cref="WorkedExamples"/> — các VÍ DỤ TÍNH THỬ người dùng ĐÃ XÁC NHẬN cho quy tắc định lượng,
///    nguồn để AI Design Spec đúc thành "## 13. Worked Examples" và POC tự kiểm đối chiếu độc lập.
/// Cả hai là câu ngắn, rỗng khi hội thoại chưa có gì tương ứng; cả hai được LƯU dạng JSON và đọc lại qua
/// <c>InterviewOutlookParser</c>.
///
/// <para>
/// PHẠM VI MÀN HÌNH từng là danh sách thứ ba của chính lời gọi này. Nó đã tách ra thành
/// <see cref="InterviewScope"/> — một lời gọi riêng chạy THƯA hơn hẳn; xem class đó cho lý do.
/// </para>
/// </summary>
public class InterviewOutlook
{
    public List<OpenQuestionEntry> OpenQuestions { get; set; } = new();
    public List<string> WorkedExamples { get; set; } = new();
}
