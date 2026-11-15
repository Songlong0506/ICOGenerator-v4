namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Kết quả của lượt RÁP BẢNG BÁO CÁO (<c>ReportMapDraftService</c>): các báo cáo người dùng đã chọn / đã kể
/// trong hội thoại, mỗi báo cáo một <see cref="ReportMapRow"/>. Đi thẳng vào <c>Project.ReportMap</c> —
/// người dùng không rà bảng này, họ rà các MÀN HÌNH báo cáo mà nó gieo ra ở bảng màn hình.
/// </summary>
public class ReportMapDraft
{
    public List<ReportMapRow> Reports { get; set; } = new();
}
