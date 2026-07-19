namespace ICOGenerator.Contracts.Requirements;

// Kết quả chấm hình ảnh POC của agent UI/UX (vision): mỗi phần tử issues/warnings là một câu mô tả
// khiếm khuyết BỐ CỤC/DỮ LIỆU MẪU nhìn thấy trên ảnh chụp — thứ mà audit tĩnh và self-test không thấy
// (màn hình trống, layout vỡ, bảng tràn, chữ đè, sai ngôn ngữ UI). Xem PocVisualReviewer.
public class PocVisualReviewResult
{
    // Lỗi phải sửa: màn hình trống trơn/thiếu dữ liệu mẫu, layout vỡ rõ rệt, sai ngôn ngữ so với spec.
    public List<PocVisualFinding> Issues { get; set; } = new();

    // Điểm nên cải thiện nhưng không chặn (khoảng cách chưa cân, màu chưa nhất quán…).
    public List<PocVisualFinding> Warnings { get; set; } = new();
}

// Một phát hiện gắn với màn hình cụ thể để agent Developer biết sửa ở đâu.
public class PocVisualFinding
{
    // Nhãn màn hình (data-view) nơi thấy vấn đề; rỗng nếu vấn đề chung toàn app.
    public string Screen { get; set; } = "";

    // Mô tả ngắn, cụ thể, tự đứng được — nói rõ thấy gì và cần sửa thành gì.
    public string Detail { get; set; } = "";
}
