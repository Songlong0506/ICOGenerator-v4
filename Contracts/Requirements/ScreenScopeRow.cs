namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// MỘT chức năng trên một màn hình — dòng con của <see cref="ScreenScopeRow"/>, và là đơn vị mà bảng phân
/// quyền cấp quyền (mỗi chức năng một dòng của bảng đó).
///
/// <para>
/// Trước đây mọi chức năng của một màn hình nằm chung trong MỘT ô text ngăn bằng dấu phẩy, nên không tầng
/// nào nối được một chức năng với quyền của nó hay với bước luồng nó phụ trách. Tách thành dòng con là để
/// mỗi chức năng là một đơn vị riêng: có bước luồng riêng, có dòng phân quyền riêng.
/// </para>
///
/// <para>
/// <see cref="FlowSteps"/> nằm ở ĐÂY chứ không ở cấp màn hình, vì một bước luồng được thực hiện bởi một
/// CHỨC NĂNG chứ không phải bởi cả màn hình: "Submit kế hoạch cho HoD HR duyệt" là việc của nút gửi duyệt
/// trên Training Implement, không phải của cả trang. Gắn đúng cấp làm phép kiểm ở
/// <c>ScreenScopeMapBuilder.UncoveredActions</c> chặt hơn hẳn bản cũ.
/// </para>
/// </summary>
public class ScreenFunction
{
    /// <summary>Tên chức năng theo góc nhìn nghiệp vụ ("Xem danh sách lớp", "Chỉnh số lớp", "Gửi duyệt").</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Các BƯỚC của bảng luồng đã chốt mà chức năng này phụ trách (chép phần <c>action</c> của bước). Rỗng
    /// là hợp lệ với chức năng tra cứu/báo cáo không nằm trong luồng nào.
    /// </summary>
    public List<string> FlowSteps { get; set; } = new();

    /// <summary>
    /// Chức năng này có thuộc ứng dụng không. Lượt soạn của BA luôn ra <c>true</c>; <c>false</c> chỉ còn ở
    /// dữ liệu của dự án cũ, thời bảng màn hình còn bày ra cho người dùng bỏ tích — dòng ấy ở lại làm BIA.
    /// </summary>
    public bool Included { get; set; } = true;

    /// <summary>
    /// Chức năng này đã được CHỐT chưa — xem <see cref="ScreenScopeRow.ConfirmedByUser"/> cho luật chung và
    /// lý do tên cờ giữ nguyên.
    ///
    /// <para>
    /// Phần trôi của phạm vi không chỉ là màn hình mới: một CHỨC NĂNG lộ ra ở lượt 30 trên một màn hình đã
    /// chốt từ lượt 23 cũng là phạm vi trôi (ca thật — dự án Locker: người dùng thêm trạng thái Need Clean,
    /// FCM bấm "Đã clean"). Có cờ ở đây thì chức năng mới là một mục CHƯA CHỐT y như một màn hình mới, và
    /// lượt soạn kế tiếp gắn bước luồng cho nó.
    /// </para>
    /// </summary>
    public bool ConfirmedByUser { get; set; }
}

/// <summary>
/// MỘT dòng của "bảng màn hình": một MÀN HÌNH của ứng dụng, việc nó làm, và các chức năng trên đó. Đây là
/// THIẾT KẾ của BA (<c>ScreenScopeDraftService</c> soạn và chốt), không phải thứ người dùng rà — họ góp ý
/// về màn hình ở POC Review, nơi họ thấy màn hình thật.
///
/// <para>
/// Vì sao bảng này phải đứng TRƯỚC bảng phân quyền: các DÒNG của bảng phân quyền lấy từ chính bảng này, và
/// AI Design Spec dựng đúng các màn hình này ở <c>## 6. Screens To Generate</c>. Bảng là NGUỒN DUY NHẤT của
/// phạm vi màn hình, và <see cref="ConfirmedByUser"/> là thứ phân biệt phần đã chốt với phần vừa lộ ra. Xem
/// <c>ScreenScopeMapBuilder.Merge</c> cho đường một chiều đưa mục mới vào bảng, và <c>ScreenScopeGate</c>
/// cho lúc phạm vi được soạn.
/// </para>
///
/// <para>
/// Dòng của bảng là MÀN HÌNH, không phải "màn hình hoặc tính năng". Ranh giới đó là chốt chặn chứ không
/// phải chuyện chữ nghĩa: cột <see cref="Screen"/> là khóa nối sang bảng phân quyền và sang các màn của bản
/// demo, nên một mục kiểu "Tính năng Generate Training Implement từ Training Plan Detail" hay "Luồng đăng ký
/// khóa học với trạng thái pending/enroll/waitlist" lọt vào đây sẽ thành một dòng phân quyền và một màn
/// hình POC — trong khi nó vốn là CHỨC NĂNG của một màn hình đã có. Chỗ đúng của chúng là
/// <see cref="Functions"/>, và <see cref="Covers"/> là cách nói ra rằng chúng đã được gộp vào đâu.
/// </para>
///
/// <para>
/// Dòng KHÔNG mang cờ bằng chứng (<c>locked</c>/<c>evidence</c>) như <see cref="PermissionMatrixRow"/>:
/// phạm vi màn hình là thiết kế, không phải lời người dùng, nên không có gì để trích dẫn.
/// </para>
/// </summary>
public class ScreenScopeRow
{
    /// <summary>
    /// Tên màn hình. Bản chuẩn hoá luôn lấy lại đúng chữ của danh sách cho phép chứ không lấy chữ của model
    /// — cùng luật với <see cref="PermissionMatrixRow.Screen"/> và với bảng cột, và cùng lý do: một dòng bịa
    /// lọt qua là một tính năng ngoài phạm vi đi vào tài liệu. Danh sách cho phép là các dòng CÒN TÍCH của
    /// chính bảng đang lưu — xem <c>ScreenScopeMapBuilder.Build</c>.
    /// </summary>
    public string Screen { get; set; } = "";

    /// <summary>
    /// Màn hình này để làm gì, một câu theo góc nhìn nghiệp vụ. BA ĐIỀN — đây là văn xuôi của BA, không
    /// phải lời người dùng, nên nó đứng riêng có gắn xuất xứ trong khối ngữ cảnh và không bao giờ được dùng
    /// làm một vế mâu thuẫn với điều người dùng nói (ca thật ghi ở ghi chú class của
    /// <c>ScreenScopeMapBuilder</c>).
    /// </summary>
    public string Purpose { get; set; } = "";

    /// <summary>
    /// Các chức năng trên màn hình, MỖI CHỨC NĂNG MỘT DÒNG — xem <see cref="ScreenFunction"/> cho lý do tách
    /// khỏi ô text cũ, và vì sao bước luồng nằm ở cấp này.
    /// </summary>
    public List<ScreenFunction> Functions { get; set; } = new();

    /// <summary>
    /// Các MỤC PHẠM VI đã được gộp vào màn hình này thay vì đứng thành dòng riêng — thứ mà lượt chắt lọc trả
    /// về dưới dạng "Tính năng …" / "Luồng …" nhưng thực chất là chức năng của màn hình này.
    ///
    /// <para>
    /// Không có trường này thì việc gộp không thể xảy ra: <c>ScreenScopeMapBuilder</c> BỔ SUNG mọi mục phạm
    /// vi mà không dòng nào nhắc tới (chốt chặn "màn hình bị bỏ quên"), nên một mục vừa được gộp vào cột
    /// chức năng sẽ lập tức mọc lại thành một dòng trắng ngay bên dưới. <see cref="Covers"/> là lời khai
    /// tất định "mục này đã có chỗ rồi".
    /// </para>
    /// </summary>
    public List<string> Covers { get; set; } = new();

    /// <summary>
    /// Màn hình này có thuộc ứng dụng không. Lượt soạn của BA luôn ra <c>true</c>; <c>false</c> chỉ còn ở dữ
    /// liệu của dự án cũ, thời bảng màn hình còn bày ra cho người dùng bỏ tích. Dòng ấy ở lại vĩnh viễn làm
    /// BIA: không lượt chắt lọc nào được phép dựng lại một màn hình đã bị loại.
    /// </summary>
    public bool Included { get; set; } = true;

    /// <summary>
    /// Dòng này do CHÍNH NGƯỜI DÙNG thêm vào bảng — chỉ còn ở dữ liệu của dự án cũ, thời bảng màn hình còn
    /// có nút "thêm màn hình". Giữ qua mọi lần soạn lại (<c>ScreenScopeMapBuilder.Build</c> chép cờ từ dòng
    /// đang lưu) để không mất dấu nguồn gốc của dòng; không đường nào ghi mới cờ này.
    ///
    /// <para>
    /// Dòng mà lượt xếp chỗ dựng ra để nhận một bước luồng mồ côi (<c>ScreenScopeMapBuilder.ApplyPlacements</c>)
    /// KHÔNG mượn cờ này: đó là thiết kế của BA, thứ bảo lãnh cho nó là phép kiểm tất định
    /// <c>UncoveredActions</c>.
    /// </para>
    /// </summary>
    public bool AddedByUser { get; set; }

    /// <summary>
    /// Dòng này đã được CHỐT chưa — cờ chia bảng làm hai phần.
    ///
    /// <para>
    /// <b>Tên cờ giữ nguyên vì nó đã nằm trong JSON lưu ở DB</b>, nhưng người đóng dấu nay là lượt soạn của
    /// BA (<c>ScreenScopeMapBuilder.Settle</c>, gọi từ <c>ScreenScopeDraftService</c>), không còn là người
    /// dùng bấm gửi bảng. Ba trạng thái:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>ConfirmedByUser=false</c> — mục vừa lộ ra từ hội thoại (hoặc do bảng báo cáo gieo sang) mà
    ///   chưa qua lượt soạn. Nó là ĐIỀU KIỆN MỞ của <c>ScreenScopeGate</c>.</item>
    ///   <item><c>ConfirmedByUser=true, Included=true</c> — đã chốt. Đây là phạm vi thật của ứng dụng: nguồn
    ///   dòng của bảng phân quyền, của <c>## 6. Screens To Generate</c> và của bản demo.</item>
    ///   <item><c>ConfirmedByUser=true, Included=false</c> — đã LOẠI (dự án cũ). Dòng ở lại vĩnh viễn làm
    ///   BIA.</item>
    /// </list>
    ///
    /// <para>
    /// Model không được phép tự bật cờ: <c>Build</c> chỉ chép nó từ các dòng ĐANG LƯU, mọi dòng model đề
    /// xuất đều ra <c>false</c> — nếu không, một model điền <c>true</c> cho có là đóng cổng soạn trước khi
    /// mục đó được mô tả và xếp bước luồng.
    /// </para>
    /// </summary>
    public bool ConfirmedByUser { get; set; }
}
