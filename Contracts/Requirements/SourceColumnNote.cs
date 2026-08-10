namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// MỘT dòng trong "bảng cột" của một tài liệu nguồn dạng bảng tính: tên cột như trong file, cách BA hiểu
/// cột đó, và cột này có được dùng trong ứng dụng mới hay không.
///
/// <para>
/// Vì sao cần một bảng thay vì hàng chip: file người dùng gửi gần như luôn là bản XUẤT của hệ thống họ
/// đang dùng, nên nó chở theo cả cột nghiệp vụ lẫn cột hạ tầng của hệ cũ (Revision Number, Preferred Time
/// zone…). Hàng chip chỉ nêu được tập con do BA tự chọn — các cột không lên chip bị coi là "của hệ cũ" mà
/// người dùng không bao giờ nhìn thấy để phản đối. Bảng phơi ĐỦ các cột của file, nên quyết định loại bỏ
/// trở thành thứ nhìn thấy được và bấm lại được.
/// </para>
///
/// <para>
/// <see cref="Meaning"/> do BA ĐIỀN SẴN, người dùng chỉ sửa dòng nào sai. Đây là ranh giới sống còn của cả
/// tính năng: một bảng 18 dòng với ô ý nghĩa để trống là bắt người dùng nghiệp vụ giải nghĩa 18 cột — đúng
/// thái cực mà <c>requirement-chat.v4.md</c> cấm, và bị hỏi "Last Name nghĩa là gì" thì họ hiểu là BA chưa
/// mở file. BA đã có tên cột, toàn bộ giá trị phân biệt và số dòng từ khối "Thống kê cột" nên đoán được
/// gần hết; đoán sai cũng lời, vì đính chính một dòng rẻ hơn viết ra cả 18 dòng.
/// </para>
/// </summary>
public class SourceColumnNote
{
    /// <summary>
    /// Tên file bảng tính chứa cột này, đúng như trong "[Nguồn: ...]" — khóa ghép về đúng
    /// <see cref="ICOGenerator.Domain.ProjectSourceFile"/>. Người dùng có thể gửi nhiều bảng tính một lượt,
    /// và hai file khác nhau hoàn toàn có thể có cột trùng tên.
    /// </summary>
    public string FileName { get; set; } = "";

    /// <summary>Tên cột ĐÚNG như hàng tiêu đề trong file (bản chuẩn hoá luôn lấy lại đúng chính tả của file).</summary>
    public string Column { get; set; } = "";

    /// <summary>
    /// Cách hiểu của BA về cột, viết như một ĐỀ XUẤT để người dùng gật hoặc sửa. Rỗng nghĩa là BA không
    /// đoán nổi — chấp nhận được với vài cột, nhưng một bảng toàn ô rỗng là lượt hỏng (xem ghi chú class).
    /// </summary>
    public string Meaning { get; set; } = "";

    /// <summary>
    /// Cột có được dùng trong ứng dụng mới không. BA TÍCH SẴN theo đề xuất của mình; người dùng bỏ tích các
    /// cột thuộc về hệ cũ. Không chốt được vế này thì text bóc từ file vẫn đi tiếp vào "dữ liệu mẫu thật"
    /// của bước sinh spec và POC seed màn hình bằng đúng các cột đó — người dùng mở demo ra thấy
    /// <c>Revision Number</c> nằm như một trường của app mới.
    /// </summary>
    public bool Used { get; set; }
}
