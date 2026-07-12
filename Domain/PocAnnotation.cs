using ICOGenerator.Domain.Enums;

namespace ICOGenerator.Domain;

/// <summary>
/// Một góp ý ĐIỂM TRÊN GIAO DIỆN của POC: người xem click vào một phần tử trong trang mockup và ghi
/// nhận xét ("nút này phải màu đỏ", "thiếu cột ngày tạo"...). Phần tử được chụp lại bằng nhãn đọc được
/// (<see cref="ElementLabel"/>) + đường dẫn CSS gần đúng (<see cref="ElementPath"/>) — POC được sinh
/// lại nhiều lần nên đây là dấu vết mô tả, không phải neo cứng. Các annotation mở được gom thành một
/// yêu cầu chỉnh sửa POC có cấu trúc (xem ApplyPocAnnotationsRevisionUseCase).
/// </summary>
public class PocAnnotation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public string AuthorUsername { get; set; } = string.Empty;

    /// <summary>Nhãn đọc được của phần tử được chọn, vd "Nút \"Lưu\"" hay "Ô nhập \"Tên khách hàng\"".</summary>
    public string ElementLabel { get; set; } = string.Empty;

    /// <summary>Đường dẫn CSS gần đúng tới phần tử (vd "form > div:nth-of-type(2) > button"). Chỉ để tham khảo.</summary>
    public string? ElementPath { get; set; }

    public string Comment { get; set; } = string.Empty;

    public PocAnnotationStatus Status { get; set; } = PocAnnotationStatus.Open;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Thời điểm chuyển Open → Submitted (gửi đội Dev). Null nếu chưa gửi.</summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>Thời điểm được gom vào yêu cầu chỉnh sửa POC. Null nếu chưa.</summary>
    public DateTime? ProcessedAt { get; set; }
}
