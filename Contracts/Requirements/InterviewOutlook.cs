namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả chắt lọc "triển vọng phỏng vấn" từ hội thoại trong MỘT lời gọi (InterviewOutlookService):
///  • <see cref="WorkedExamples"/> — các VÍ DỤ TÍNH THỬ người dùng ĐÃ XÁC NHẬN cho quy tắc định lượng,
///    nguồn để AI Design Spec đúc thành "## 13. Worked Examples" và POC tự kiểm đối chiếu độc lập.
/// Mỗi mục là một câu ngắn, rỗng khi hội thoại chưa có gì tương ứng; danh sách được LƯU dạng JSON và đọc
/// lại qua <c>InterviewOutlookParser</c>.
///
/// <para>
/// HAI danh sách đã rời khỏi lời gọi này, mỗi cái vì một nhịp riêng:
/// <list type="bullet">
///   <item>PHẠM VI MÀN HÌNH → <see cref="InterviewScope"/>, một lời gọi chạy THƯA hơn hẳn.</item>
///   <item>ĐIỂM CẦN LÀM RÕ → lượt chắt lọc bản đồ bao phủ (<see cref="CoverageDistillDocument"/>), chạy
///         TRONG lượt chat. Danh sách câu hỏi và bản đồ ràng buộc nhau chặt tới mức chúng chỉ đúng khi
///         được viết cùng nhau; chắt ở hậu kỳ như đây thì nó luôn cũ hơn bản đồ đúng một lượt.</item>
/// </list>
/// Còn lại đúng một danh sách vì nó đi theo nhịp ngược lại: <see cref="WorkedExamples"/> chỉ được tiêu
/// thụ ở bước sinh AI Design Spec, nên nó không cần tươi trong lượt chat và ở lại hậu kỳ để không cộng
/// vào độ chờ cảm nhận.
/// </para>
/// </summary>
public class InterviewOutlook
{
    public List<string> WorkedExamples { get; set; } = new();
}
