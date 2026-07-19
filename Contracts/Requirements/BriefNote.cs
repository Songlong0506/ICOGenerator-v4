namespace ICOGenerator.Contracts.Requirements;

// Một ghi chú người dùng gắn vào MỘT đoạn trong bản xem trước Product Brief: đoạn được chọn (Quote) +
// điều cần sửa (Note). Gom các ghi chú này thành phản hồi để BA sửa lại brief (xem ReviseBriefFromNotesUseCase).
public class BriefNote
{
    // Đoạn văn user bôi đen trong brief (ngữ cảnh để BA biết sửa ở chỗ nào). Có thể rỗng nếu ghi chú chung.
    public string Quote { get; set; } = "";

    // Điều cần sửa cho đoạn đó.
    public string Note { get; set; } = "";
}
