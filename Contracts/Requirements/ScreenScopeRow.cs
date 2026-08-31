namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// MỘT chức năng trên một màn hình — dòng con của <see cref="ScreenScopeRow"/>, và là đơn vị mà người dùng
/// tích / bỏ tích.
///
/// <para>
/// Trước đây mọi chức năng của một màn hình nằm chung trong MỘT ô text ngăn bằng dấu phẩy. Ô đó đọc thì
/// được, nhưng nó chở một quyết định mà không ai bấm được: muốn loại đúng một chức năng, người dùng phải
/// sửa tay giữa một chuỗi chữ — thao tác đó không để lại dấu vết nào máy đọc được — còn bỏ tích cả màn hình
/// thì mất luôn những chức năng họ vẫn cần. Tách thành dòng con là để mỗi chức năng có ô tích của riêng nó,
/// đúng đơn vị người dùng thật sự muốn giữ hay bỏ.
/// </para>
///
/// <para>
/// <see cref="FlowSteps"/> nằm ở ĐÂY chứ không ở cấp màn hình, vì một bước luồng được thực hiện bởi một
/// CHỨC NĂNG chứ không phải bởi cả màn hình: "Submit kế hoạch cho HoD HR duyệt" là việc của nút gửi duyệt
/// trên Training Implement, không phải của cả trang. Gắn đúng cấp làm phép kiểm ở
/// <c>ScreenScopeMapBuilder.UncoveredActions</c> chặt hơn hẳn bản cũ: bỏ tích một chức năng thì bước nó
/// phụ trách lập tức thành bước chưa ai làm, và người dùng thấy điều đó ngay lúc bảng còn trên màn hình.
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
    /// Chức năng này có thuộc ứng dụng không. BA TÍCH SẴN theo đề xuất của mình; người dùng bỏ tích thứ họ
    /// không cần. Bỏ tích chứ không xóa: dòng bị loại vẫn phải kể lại được trong tin nhắn gửi đi.
    /// </summary>
    public bool Included { get; set; } = true;

    /// <summary>
    /// Chức năng này đã đi qua tay NGƯỜI DÙNG chưa — xem <see cref="ScreenScopeRow.ConfirmedByUser"/> cho
    /// luật chung; ở cấp này nó còn mở ra một ca mà trước đây hệ thống không biểu diễn nổi.
    ///
    /// <para>
    /// Phần trôi của phạm vi không chỉ là màn hình mới: một CHỨC NĂNG lộ ra ở lượt 30 trên một màn hình đã
    /// chốt từ lượt 23 cũng là phạm vi trôi, và nó đi thẳng vào tài liệu mà không ai rà — bản cũ chỉ so
    /// được TÊN MÀN HÌNH nên cả màn hình ấy vẫn "đã biết" và cổng không mở lại. Có cờ ở đây thì chức năng
    /// mới là một mục CHỜ DUYỆT y như một màn hình mới, và bảng bày lại đủ để người dùng gật hay bỏ tích.
    /// </para>
    /// </summary>
    public bool ConfirmedByUser { get; set; }
}

/// <summary>
/// MỘT dòng của "bảng màn hình": một MÀN HÌNH dự kiến của ứng dụng, việc nó làm, và các chức năng trên đó.
///
/// <para>
/// Vì sao bảng này tồn tại, và vì sao nó phải đứng TRƯỚC bảng phân quyền: các DÒNG của bảng phân quyền lấy
/// từ chính bảng này. Trước đây chúng lấy từ một danh sách bullet riêng (<c>Project.PlannedScope</c>, đã
/// gỡ) do LLM chắt sau mỗi lượt chat mà người dùng KHÔNG bao giờ nhìn thấy. Nghĩa là toàn bộ phần phân
/// quyền, thứ đã được dựng cẩn thận để có bằng chứng trên từng ô, lại đang đứng trên một danh sách màn
/// hình chưa ai duyệt: một màn hình LLM chắt nhầm sẽ được người dùng tích quyền cho, và một màn hình bị bỏ
/// quên thì không bao giờ có mặt để họ phản đối.
/// </para>
///
/// <para>
/// Nay bảng này là NGUỒN DUY NHẤT của phạm vi màn hình, và <see cref="ConfirmedByUser"/> là thứ phân biệt
/// phần đã được rà với phần vừa lộ ra. Xem <c>ScreenScopeMapBuilder.Merge</c> cho đường một chiều đưa mục
/// mới vào bảng, và <c>ScreenScopeGate</c> cho lúc bảng được bày ra hỏi.
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
/// Dòng KHÔNG mang cờ bằng chứng (<c>locked</c>/<c>evidence</c>) như <see cref="PermissionMatrixRow"/>: ở
/// bảng này mọi dòng đều tích sẵn nên cờ ấy không đổi được trạng thái ô nào, nó chỉ vẽ thêm một dấu ✓ có
/// tooltip trích dẫn. Lý do gỡ nằm ở <c>docs/requirement-flow.md</c>, mục
/// "Vì sao bảng màn hình không có dấu ✓ bằng chứng".
/// </para>
/// </summary>
public class ScreenScopeRow
{
    /// <summary>
    /// Tên màn hình. Bản chuẩn hoá luôn lấy lại đúng chữ của danh sách cho phép chứ không lấy chữ của model
    /// — cùng luật với <see cref="PermissionMatrixRow.Screen"/> và với bảng cột, và cùng lý do: một dòng bịa
    /// lọt qua là một tính năng ngoài phạm vi đi vào tài liệu mang chữ ký người dùng. Danh sách cho phép là
    /// các dòng CÒN TÍCH của chính bảng đang lưu ở lượt BÀY BẢNG, nhưng là bảng server đã render ở đường
    /// GỬI — xem <c>ScreenScopeMapBuilder.Sanitize</c>.
    /// </summary>
    public string Screen { get; set; } = "";

    /// <summary>
    /// Màn hình này để làm gì, một câu theo góc nhìn nghiệp vụ. BA ĐIỀN SẴN — một bảng ô trống là bắt người
    /// dùng nghiệp vụ tự viết đặc tả cho mười mấy màn hình, đúng thái cực mà bảng cột đã cấm.
    ///
    /// <para>
    /// "BA điền sẵn" kéo theo một hệ quả: ô này KHÔNG phải quyết định của người dùng, nên nó không đi vào
    /// bản kể gửi lên khung chat và đứng riêng có gắn xuất xứ trong khối ngữ cảnh — cùng luật với ô mô tả
    /// của <c>EntityMapRow</c>, ca thật ghi ở ghi chú class của <c>EntityMapBuilder</c>.
    /// </para>
    /// </summary>
    public string Purpose { get; set; } = "";

    /// <summary>
    /// Các chức năng trên màn hình, MỖI CHỨC NĂNG MỘT DÒNG có ô tích riêng — xem <see cref="ScreenFunction"/>
    /// cho lý do tách khỏi ô text cũ, và vì sao bước luồng nằm ở cấp này.
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
    /// tất định "mục này đã có chỗ rồi", và nó phải hiện trên bảng: một mục biến mất khỏi phạm vi mà người
    /// dùng không nhìn thấy là đúng loại quyết định thay họ mà cả bảng này sinh ra để chặn.
    /// </para>
    /// </summary>
    public List<string> Covers { get; set; } = new();

    /// <summary>
    /// Màn hình này có thuộc ứng dụng không. BA TÍCH SẴN theo đề xuất của mình; người dùng bỏ tích thứ họ
    /// không cần. Bỏ tích chứ không xóa: dòng bị loại vẫn phải kể lại được trong tin nhắn gửi đi, nếu không
    /// người dùng không có bằng chứng nào cho thấy mình vừa loại đúng thứ định loại.
    /// </summary>
    public bool Included { get; set; } = true;

    /// <summary>
    /// Dòng này do CHÍNH NGƯỜI DÙNG thêm vào bảng bằng nút "thêm màn hình", không phải do BA đề xuất.
    ///
    /// <para>
    /// Cờ tồn tại vì chốt chặn "màn hình bịa" (<c>ScreenScopeMapBuilder.MatchScreen</c>) loại mọi dòng không
    /// khớp danh sách cho phép — nó được dựng để chặn MODEL, và nếu không phân biệt được nguồn thì nó chặn
    /// luôn màn hình người dùng vừa tự gõ: họ thêm một dòng, bấm gửi, và dòng ấy biến mất không một lời nào
    /// nói vì sao. Chốt chặn giữ nguyên với mọi dòng còn lại; chỉ dòng mang cờ này mới đi vòng, và chỉ ở
    /// đường GỬI (<c>ScreenScopeMapBuilder.Sanitize</c>) — lượt BÀY BẢNG là đề xuất của model, ở đó một cờ
    /// "người dùng tự thêm" chỉ là chỗ để model tự cấp phép cho mình.
    /// </para>
    ///
    /// <para>
    /// Có một đường THỨ HAI đưa được màn hình mới vào bảng, và nó KHÔNG mượn cờ này:
    /// <c>ScreenScopeMapBuilder.ApplyPlacements</c> dựng một dòng để nhận bước luồng đã chốt mà không màn
    /// hình nào đang có phụ trách nổi. Dòng ấy là đề xuất của BA nên cờ vẫn <c>false</c>; thứ bảo lãnh cho
    /// nó là phép kiểm tất định <c>UncoveredActions</c>, không phải chữ ký người dùng.
    /// </para>
    ///
    /// <para>
    /// Cờ được GIỮ qua đường lưu, vì
    /// <c>ScreenScopeMapBuilder.RenderUserMessage</c> dùng nó để kể lại "các màn hình mình tự bổ sung": một
    /// màn hình chưa từng có trong đề xuất mà lặng lẽ đi vào phạm vi là đúng loại thay đổi phải nói ra, cùng
    /// luật với các dòng bị bỏ tích.
    /// </para>
    /// </summary>
    public bool AddedByUser { get; set; }

    /// <summary>
    /// Dòng này đã đi qua tay NGƯỜI DÙNG chưa — cờ chia bảng làm hai phần, và là thứ thay cho cả một danh
    /// sách phạm vi song song từng tồn tại (<c>Project.PlannedScope</c>).
    ///
    /// <para>
    /// Ba trạng thái, và mỗi trạng thái là một câu khác hẳn nhau:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>ConfirmedByUser=false</c> — mục vừa lộ ra từ hội thoại (hoặc do một bảng khác gieo sang)
    ///   mà chưa ai rà. Nó là ĐIỀU KIỆN MỞ của <c>ScreenScopeGate</c>: còn một mục như thế thì bảng còn
    ///   phải được bày ra hỏi.</item>
    ///   <item><c>ConfirmedByUser=true, Included=true</c> — người dùng đã GIỮ. Đây là phạm vi thật của ứng
    ///   dụng: nguồn dòng của bảng phân quyền, của <c>## 6. Screens To Generate</c> và của bản demo.</item>
    ///   <item><c>ConfirmedByUser=true, Included=false</c> — người dùng đã LOẠI. Dòng ở lại vĩnh viễn làm
    ///   BIA: không lượt chắt lọc nào được phép dựng lại một màn hình họ vừa đóng, và không có bia thì lần
    ///   sau nó quay lại làm một mục "mới" tinh.</item>
    /// </list>
    ///
    /// <para>
    /// Cờ được đóng dấu ở ĐÚNG MỘT chỗ — <c>ScreenScopeMapBuilder.Sanitize</c>, tức đường GỬI của bảng —
    /// và không bao giờ bị gỡ xuống. Model không được phép tự bật nó: <c>Build</c> chỉ chép cờ từ các dòng
    /// ĐANG LƯU, mọi dòng model đề xuất đều ra <c>false</c>, cùng luật và cùng lý do với
    /// <see cref="Included"/>.
    /// </para>
    /// </summary>
    public bool ConfirmedByUser { get; set; }
}
