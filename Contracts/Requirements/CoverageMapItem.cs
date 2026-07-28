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

    /// <summary>
    /// BẰNG CHỨNG cho kết luận của dòng này: trích ngắn điều người dùng đã nói/tài liệu đã ghi mà trạng
    /// thái dựa vào (khối <c>{nguồn: …}</c> ở cuối tóm tắt). Không có bằng chứng thì người dùng không có
    /// cách nào biết vì sao một nhóm bị chấm [RÕ] — và một nhóm bị chấm [RÕ] oan thì BA sẽ KHÔNG BAO GIỜ
    /// hỏi lại nó nữa. Rỗng với bản đồ cũ (sinh trước khi format có mục này) — UI chỉ đơn giản không hiện.
    /// </summary>
    public string Evidence { get; set; } = string.Empty;

    public bool IsCore { get; set; }
}
