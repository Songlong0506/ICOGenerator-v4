namespace ICOGenerator.Contracts.Requirements;

// Một bước trong sơ đồ luồng nghiệp vụ mà BA vẽ ở lượt mời "Write Requirement" để user xác nhận trực
// quan (vai trò → hành động → kết quả/trạng thái). Người nghiệp vụ bắt lỗi luồng trên sơ đồ tốt hơn
// nhiều so với đọc đoạn văn tóm tắt — cùng triết lý "chốt công thức bằng ví dụ số" nhưng cho luồng.
public class FlowStep
{
    // Ai thực hiện bước này (vd "Nhân viên", "Quản lý", "Hệ thống"). Rỗng nếu không gắn vai cụ thể.
    public string Actor { get; set; } = "";

    // Hành động ở bước này (vd "Gửi đơn nghỉ phép").
    public string Action { get; set; } = "";

    // Kết quả/trạng thái sau bước (vd "Đơn ở trạng thái Chờ duyệt"). Rỗng nếu không cần nêu.
    public string Outcome { get; set; } = "";
}
