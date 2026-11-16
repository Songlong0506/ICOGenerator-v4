namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả của lượt RÁP BẢNG BÁO CÁO (<c>ReportMapDraftService</c>): các báo cáo người dùng đã chọn / đã kể
/// trong hội thoại, mỗi báo cáo một <see cref="ReportMapRow"/>. Đi thẳng vào <c>Project.ReportMap</c> —
/// người dùng không rà bảng này, họ rà thứ nó gieo ra ở bảng màn hình: màn tổng quan gom các báo cáo (mỗi
/// báo cáo một chức năng) và màn riêng của báo cáo nào cần.
/// </summary>
public class ReportMapDraft
{
    /// <summary>
    /// Tên MÀN TỔNG QUAN gom các báo cáo, tiếng Anh 2–3 từ ("Locker Dashboard") — thành nhãn mục menu của
    /// bản demo. Rỗng thì <c>ReportMapBuilder.DashboardName</c> dùng tên mặc định.
    /// </summary>
    public string Dashboard { get; set; } = "";

    public List<ReportMapRow> Reports { get; set; } = new();
}
