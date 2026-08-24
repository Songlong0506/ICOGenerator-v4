namespace ICOGenerator.Contracts.Requirements;

// Một bước trong sơ đồ luồng nghiệp vụ mà BA TỪNG vẽ ở lượt mời "Write Requirement" (vai trò → hành
// động → kết quả). Sơ đồ đó đã GỠ — luồng nay được chốt bằng BẢNG LUỒNG, nơi từng bước sửa được và bỏ
// được (xem FlowMapStep). Lớp này ở lại đúng một việc: đọc lại cột AgentConversation.FlowDiagram của các
// hội thoại CŨ cho bản xuất và transcript.
public class FlowStep
{
    // Ai thực hiện bước này (vd "Nhân viên", "Quản lý", "Hệ thống"). Rỗng nếu không gắn vai cụ thể.
    public string Actor { get; set; } = "";

    // Hành động ở bước này (vd "Gửi đơn nghỉ phép").
    public string Action { get; set; } = "";

    // Kết quả/trạng thái sau bước (vd "Đơn ở trạng thái Chờ duyệt"). Rỗng nếu không cần nêu.
    public string Outcome { get; set; } = "";
}
