namespace ICOGenerator.Domain.Enums;

/// <summary>
/// Vòng đời một biến thể POC thử model (xem <see cref="PocVariant"/>). Lưu DB dạng chuỗi ⇒ không đổi tên
/// giá trị đã có dữ liệu.
/// </summary>
public enum PocVariantStatus
{
    Queued,
    Running,
    Completed,
    Failed
}
