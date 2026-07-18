namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Một dòng của "Bản đồ bao phủ yêu cầu" đã parse để UI render panel tiến độ: nhóm thông tin, trạng
/// thái khai thác ([RÕ]/[MỘT PHẦN]/[CHƯA HỎI]/[KHÔNG ÁP DỤNG]), tóm tắt ngắn và cờ nhóm cốt lõi (★).
/// </summary>
public class CoverageMapItem
{
    public string Label { get; set; } = string.Empty;

    /// <summary>Trạng thái đã chuẩn hoá: "RÕ" | "MỘT PHẦN" | "CHƯA HỎI" | "KHÔNG ÁP DỤNG".</summary>
    public string Status { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public bool IsCore { get; set; }
}
