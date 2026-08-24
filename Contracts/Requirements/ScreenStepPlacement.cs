namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// CHỖ Ở của một bước luồng chưa được chức năng nào phụ trách — kết quả lượt XẾP CHỖ
/// (<c>ScreenStepPlacementService</c>), chạy khi <c>ScreenScopeMapBuilder.UncoveredActions</c> tìm ra một
/// bước mồ côi ngay lúc bảng màn hình sắp hiện ra.
///
/// <para>
/// <b>Vì sao nó tồn tại.</b> Phép kiểm mối nối luồng ⇄ màn hình là tất định và nó bắt đúng lỗi cần bắt,
/// nhưng phần còn lại thì trước đây đẩy sang người dùng: dòng nhắc dưới bảng bảo họ *"điền bước đó vào ô
/// bên phải của chức năng phù hợp, hoặc nhắn cho mình biết nếu thiếu hẳn một màn hình"*. Đó là đúng phần
/// việc của BA hỏi ngược người đi thuê BA — họ vừa rà xong một bảng mười bảy màn hình và không có cơ sở
/// nào để biết *"Xem danh sách nhân viên trực tiếp dưới quyền"* thuộc màn nào. Ca thật (dự án JD Library
/// 2): bước đó là bước 4 của luồng chính người dùng đã tự tay chốt, chỗ đúng của nó là một chức năng trên
/// màn <c>JD Assignment</c> — thứ BA thừa dữ kiện để tự xếp.
/// </para>
///
/// <para>
/// <b>Phân vai giữa máy và model.</b> Code quyết định CÓ lỗ hổng không (<c>UncoveredActions</c>) và code
/// quyết định lời xếp chỗ có được nhận không (<c>ScreenScopeMapBuilder.ApplyPlacements</c>); model chỉ trả
/// lời đúng một câu ngữ nghĩa mà không phép so chuỗi nào làm thay được: <b>bước này là việc của chức năng
/// nào</b>. Kết quả không đi thẳng vào tài liệu — nó thành một dòng TÍCH SẴN trên chính bảng người dùng
/// đang rà, y như mọi đề xuất khác của BA, nên người dùng vẫn là người chốt.
/// </para>
/// </summary>
public class ScreenStepPlacement
{
    /// <summary>
    /// Bước luồng cần xếp chỗ — phải CHÉP ĐÚNG một mục trong danh sách bước mồ côi đã đưa cho model.
    /// Không khớp ⇒ lời xếp chỗ bị bỏ: lượt này chỉ được lấp lỗ hổng, không được nhân dịp viết lại bảng.
    /// </summary>
    public string Step { get; set; } = "";

    /// <summary>
    /// Màn hình nhận bước này. Khớp một dòng đang có ⇒ bước về dòng đó. KHÔNG khớp dòng nào ⇒ đây là màn
    /// hình MỚI, và nó được nhận — ngoại lệ DUY NHẤT của chốt chặn "màn hình bịa", xem
    /// <c>ScreenScopeMapBuilder.ApplyPlacements</c>.
    /// </summary>
    public string Screen { get; set; } = "";

    /// <summary>
    /// Chức năng phụ trách bước. Trùng tên một chức năng đang có trên màn đó ⇒ bước gắn thêm vào chức năng
    /// ấy; không trùng ⇒ một chức năng MỚI được thêm vào cuối màn, mang đúng bước này.
    /// </summary>
    public string Function { get; set; } = "";

    /// <summary>
    /// Việc của màn — CHỈ dùng khi <see cref="Screen"/> là một màn hình mới. Dòng mới mà không có câu này
    /// thì người dùng nhận một cái tên suông đúng ở chỗ họ phải quyết định giữ hay bỏ.
    /// </summary>
    public string Purpose { get; set; } = "";
}

/// <summary>Bọc danh sách <see cref="ScreenStepPlacement"/> cho structured output (đầu ra phải là object).</summary>
public class ScreenStepPlacementPlan
{
    public List<ScreenStepPlacement> Placements { get; set; } = new();
}
