using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// GIAI ĐOẠN của một dự án nhìn từ phía NGƯỜI YÊU CẦU — năm chặng của hành trình "chưa nói gì" →
/// "đã nghiệm thu bản demo". Dùng để hiển thị (badge ở danh sách dự án, đầu trang POC Review) và về
/// sau là để đếm/báo cáo.
///
/// <para>
/// KHÔNG LƯU XUỐNG DB — không có cột <c>Projects.Status</c> nào cả. Giá trị được SUY RA tại chỗ từ dữ
/// liệu đã có (xem <c>Application/Projects/ProjectStatusResolver</c>). Lý do: mỗi chặng đều đã có nguồn
/// chân lý riêng và mỗi chặng có nhiều đường ghi (duyệt Brief, soạn lại Brief từ ghi chú, gửi ghi chú POC
/// về Requirement, từ chối cổng, nhân bản dự án, nghiệm thu/rút nghiệm thu). Một cột lưu sẵn buộc MỌI
/// đường trong số đó phải nhớ cập nhật, quên một đường là badge nói dối mà không test nào bắt được —
/// còn suy ra thì không có trạng thái thứ hai để lệch.
/// </para>
///
/// <para>
/// KHÁC <see cref="WorkflowStageKey"/>: enum này kể chặng của NGƯỜI YÊU CẦU, còn WorkflowStageKey kể
/// bước KỸ THUẬT của delivery pipeline (spec → POC → implementation → PR). Toàn bộ pipeline nằm gọn
/// giữa <see cref="ProductBriefApproved"/> và <see cref="PocApproved"/>, nên hai enum bổ sung cho nhau
/// chứ không thay thế nhau.
/// </para>
/// </summary>
public enum ProjectStatus
{
    /// <summary>Chưa từng có lượt chat nào — dự án vừa tạo, chưa ai nói gì với BA.</summary>
    [Description("New")]
    New = 1,

    /// <summary>Đang phỏng vấn: đã có lượt chat nhưng chưa bấm "Write Requirement" lần nào.</summary>
    [Description("Get requirement")]
    GetRequirement = 2,

    /// <summary>Đã có Product Brief bản nháp (bấm "Write Requirement"), chưa duyệt lần nào.</summary>
    [Description("Product Brief Draft")]
    ProductBriefDraft = 3,

    /// <summary>Product Brief đã được duyệt (V{n}) — delivery pipeline chạy trong chặng này.</summary>
    [Description("Product Brief Approve")]
    ProductBriefApproved = 4,

    /// <summary>
    /// Người yêu cầu đã bấm "Approve POC". Đây cũng là chặng KHOÁ nội dung: chat BA và ghi chú POC
    /// ngừng nhận thay đổi cho tới khi bấm "Withdraw Approve" — xem <c>PocAcceptanceGate</c>.
    /// </summary>
    [Description("POC Approve")]
    PocApproved = 5
}
