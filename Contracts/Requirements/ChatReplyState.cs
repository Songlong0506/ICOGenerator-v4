namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Trạng thái "câu trả lời của BA cho lượt hiện tại" mà trang Requirements hỏi sau khi tải lại giữa
/// chừng (endpoint ChatReplyStatus).
/// </summary>
/// <param name="Pending">
/// Lượt hội thoại mới nhất là của user ⇒ chưa có câu trả lời tương ứng. UI hiện lại "BA đang soạn…".
/// </param>
/// <param name="Stale">
/// Lượt đang chờ đó ĐÃ CHẾT: không còn lượt BA nào chạy cho project này và lượt user đã cũ hơn ngưỡng
/// ân hạn — câu trả lời sẽ KHÔNG bao giờ tới (tiến trình khởi động lại giữa chừng, lỗi hạ tầng nuốt mất
/// lượt assistant). UI phải dừng chờ, mở khóa ô nhập và mời "Thử lại" thay vì treo mãi ở spinner.
/// </param>
public record ChatReplyState(bool Pending, bool Stale);
