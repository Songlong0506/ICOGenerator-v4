using System.ComponentModel;

namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Vòng đời một annotation trên POC. Lưu xuống DB dạng chuỗi (tên enum) như các enum khác — ĐỪNG đổi
/// tên các giá trị đã ghi.
/// </summary>
public enum PocAnnotationStatus
{
    /// <summary>Mới tạo — người góp ý còn sửa/xóa được.</summary>
    [Description("Mới")]
    Open,

    /// <summary>Đã gửi cho đội Dev (thông báo tới người có quyền DeliveryAdvance) — chờ xử lý.</summary>
    [Description("Đã gửi đội Dev")]
    Submitted,

    /// <summary>Đã được gom vào một yêu cầu chỉnh sửa POC gửi cho agent.</summary>
    [Description("Đã đưa vào yêu cầu chỉnh sửa")]
    Processed
}
