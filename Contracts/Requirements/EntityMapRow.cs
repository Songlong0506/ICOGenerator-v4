namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// MỘT thông tin cần lưu của một đối tượng nghiệp vụ. Cố ý KHÔNG gọi là "field": người dùng nghiệp vụ
/// không phân biệt trường dữ liệu với cột báo cáo, và hỏi họ bằng từ vựng mô hình dữ liệu là cách nhanh
/// nhất để nhận lại một cái gật cho có.
/// </summary>
public class EntityFieldNote
{
    /// <summary>Tên thông tin theo ngôn ngữ nghiệp vụ ("Ngày bắt đầu", "Người phụ trách").</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Cách hiểu của BA, viết như một ĐỀ XUẤT để người dùng gật hoặc sửa. Điền sẵn vì cùng lý do với ô ý
    /// nghĩa của bảng cột — đoán sai thì họ sửa một dòng, còn để trống là bắt họ viết đặc tả.
    /// </summary>
    public string Meaning { get; set; } = "";

    /// <summary>Thông tin này có cần lưu không. BA tích sẵn; người dùng bỏ tích thứ thừa.</summary>
    public bool Used { get; set; } = true;

    /// <summary>
    /// Người dùng có BẮT BUỘC phải điền thông tin này không.
    ///
    /// <para>
    /// Khác hẳn <see cref="Used"/> và đó là chỗ dễ nhầm nhất của bảng: <see cref="Used"/> là "ứng dụng có
    /// lưu thứ này không" — chỗ người dùng LOẠI BỚT đề xuất của BA; còn cờ này là "để trống có được không".
    /// Bỏ tích <see cref="Used"/> ⇒ cả dòng mờ đi và ô này khóa lại, vì bắt buộc nhập một thứ không được lưu
    /// là một ô không có nghĩa.
    /// </para>
    ///
    /// <para>
    /// Mặc định <b>false</b>, ngược chiều với <see cref="Used"/>: mọi thông tin đều tích sẵn "cần lưu" vì đó
    /// là đề xuất của BA chờ bị loại, nhưng "bắt buộc nhập" là một RÀNG BUỘC — POC dựng ra một biểu mẫu
    /// không gửi đi được khi thiếu ô, nên tích sẵn cả loạt là ra quyết định thay người dùng ở đúng chỗ họ
    /// đọc lướt nhất. BA chỉ tích những ô hội thoại đã nói là bắt buộc.
    /// </para>
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    /// NGƯỜI DÙNG NHẬP THẾ NÀO — một trong <see cref="EntityFieldInput"/>.
    ///
    /// <para>
    /// Giá trị KHỞI TẠO là <see cref="EntityFieldInput.Text"/> chứ không phải chuỗi rỗng, và prompt dựa
    /// thẳng vào điều đó: *"chưa ai bàn ⇒ <c>input: text</c>"*. Model bỏ trống trường này thì deserializer
    /// giữ nguyên giá trị khởi tạo; rỗng ở đây sẽ đi thẳng ra <c>ChatExportBuilder</c> và các khối ngữ cảnh
    /// dưới dạng một ô "không rõ kiểu" mà chưa ai từng hỏi.
    /// </para>
    ///
    /// <para>
    /// Đây là MỘT trong hai trục, và tách hai trục là chủ ý: một danh sách chọn còn phải nói rõ chọn MỘT hay
    /// chọn NHIỀU, nên gộp "danh sách tự nhập" vào cùng dropdown với "chọn một / chọn nhiều" sẽ đẻ ra đúng
    /// một ô không ai trả lời — trục còn lại là <see cref="Source"/>.
    /// </para>
    /// </summary>
    public string Input { get; set; } = EntityFieldInput.Text;

    /// <summary>
    /// DANH SÁCH LẤY Ở ĐÂU — một trong <see cref="EntityFieldSource"/>. Chỉ có nghĩa khi
    /// <see cref="Input"/> là <c>choice-one</c>/<c>choice-many</c>; các kiểu khác bị chuẩn hoá về rỗng
    /// (<c>EntityMapBuilder</c>), vì một nguồn danh sách gắn vào một ô gõ tay là ô người dùng phải đọc để
    /// rồi bỏ qua.
    ///
    /// <para>
    /// Rỗng là HỢP LỆ và có nghĩa riêng: "chưa chốt". Không chặn nút gửi ở ca này — cả hai bản kể
    /// (<c>RenderUserMessage</c> và khối ngữ cảnh) gọi tên đúng các ô còn thiếu để BA hỏi nốt ở lượt sau,
    /// và đó là đường thu hồi rẻ hơn hẳn một cổng gửi từ chối. Bảng thông báo là bảng DUY NHẤT được phép từ
    /// chối một bảng người dùng đã bấm gửi, và nó phải như vậy vì dòng khóa ở đó không có checkbox nào để
    /// người dùng thoát ra.
    /// </para>
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// Các giá trị của danh sách khi <see cref="Source"/> là <see cref="EntityFieldSource.Inline"/> —
    /// người dùng tự gõ ngay trên bảng, vì danh sách chỉ có vài giá trị và dựng cho nó một màn hình quản lý
    /// riêng là đắt hơn giá trị nó mang lại.
    /// </summary>
    public List<string> Options { get; set; } = new();

    /// <summary>
    /// Tên hệ thống nguồn khi <see cref="Source"/> là <see cref="EntityFieldSource.External"/>.
    ///
    /// <para>
    /// Bắt buộc phải hỏi cho ra, và đó là lý do ô này tồn tại chứ không để "lấy từ hệ thống khác" đứng một
    /// mình: một tích hợp không gọi được tên là một tích hợp KHÔNG CÓ THẬT — spec ghi một đường dữ liệu mà
    /// không ai dựng được, còn POC thì không biết lấy dữ liệu mẫu ở đâu. Repo đã trả giá đúng kiểu này một
    /// lần với các kênh thông báo mà hội thoại chưa bao giờ xác nhận là có.
    /// </para>
    /// </summary>
    public string SourceSystem { get; set; } = "";

    /// <summary>
    /// Quy tắc sinh khi <see cref="Input"/> là <see cref="EntityFieldInput.Auto"/> ("HcP-JD-XXX").
    ///
    /// <para>
    /// Một mã tự sinh mà không nói quy tắc thì POC dựng cho nó một Ô NHẬP — đúng thứ người dùng vừa nói là
    /// họ không phải nhập. Vì vậy kiểu <c>auto</c> còn kéo theo một luật nữa ở
    /// <c>EntityMapBuilder</c>: <see cref="Required"/> bị ép về <c>false</c>, vì "bắt buộc nhập" một ô
    /// người dùng không hề nhập là một ràng buộc vô nghĩa.
    /// </para>
    /// </summary>
    public string Rule { get; set; } = "";
}

/// <summary>
/// NGƯỜI DÙNG NHẬP THẾ NÀO — trục thứ nhất của một thông tin (<see cref="EntityFieldNote.Input"/>).
///
/// <para>
/// Giá trị lưu xuống DB dưới dạng CHUỖI (trong JSON của <c>Project.EntityMap</c>) nên các chuỗi ở đây
/// không được đổi — đúng luật enum-lưu-chuỗi của repo. Đây cố ý là hằng chuỗi chứ không phải <c>enum</c>:
/// dữ liệu đến từ model và từ trình duyệt, và một giá trị lạ phải chuẩn hoá được về mặc định an toàn thay
/// vì làm hỏng cả lượt deserialize.
/// </para>
/// </summary>
public static class EntityFieldInput
{
    /// <summary>Gõ tay. Mặc định, và là giá trị mà mọi bản chốt CŨ đọc ra.</summary>
    public const string Text = "text";

    /// <summary>Con số (số lượng, %, tiền).</summary>
    public const string Number = "number";

    /// <summary>Ngày/tháng.</summary>
    public const string Date = "date";

    /// <summary>Chọn MỘT giá trị trong một danh sách.</summary>
    public const string ChoiceOne = "choice-one";

    /// <summary>Chọn NHIỀU giá trị trong một danh sách.</summary>
    public const string ChoiceMany = "choice-many";

    /// <summary>Ứng dụng TỰ SINH, người dùng không nhập — xem <see cref="EntityFieldNote.Rule"/>.</summary>
    public const string Auto = "auto";

    /// <summary>Hai kiểu cần một danh sách, tức hai kiểu duy nhất mà <see cref="EntityFieldNote.Source"/> có nghĩa.</summary>
    public static bool IsChoice(string? value) => value == ChoiceOne || value == ChoiceMany;

    /// <summary>Giá trị lạ (model bịa, payload cũ, chuỗi rỗng) ⇒ <see cref="Text"/>.</summary>
    public static string Normalize(string? value) => value switch
    {
        Number => Number,
        Date => Date,
        ChoiceOne => ChoiceOne,
        ChoiceMany => ChoiceMany,
        Auto => Auto,
        _ => Text
    };
}

/// <summary>
/// DANH SÁCH LẤY Ở ĐÂU — trục thứ hai (<see cref="EntityFieldNote.Source"/>), chỉ có nghĩa khi kiểu nhập là
/// một trong hai kiểu chọn. Hằng chuỗi vì cùng lý do với <see cref="EntityFieldInput"/>.
/// </summary>
public static class EntityFieldSource
{
    /// <summary>Danh sách chỉ có vài giá trị, người dùng gõ thẳng vào bảng — xem <see cref="EntityFieldNote.Options"/>.</summary>
    public const string Inline = "inline";

    /// <summary>
    /// Ứng dụng tự quản lý danh sách ⇒ nó cần MỘT MÀN HÌNH quản lý riêng. Đây là giá trị đắt nhất của cả
    /// hai trục: nó không dừng ở mô hình dữ liệu mà đẻ ra một màn hình, nên <c>EntityMapBuilder</c> gieo nó
    /// vào phạm vi màn hình của dự án — xem <c>EntityMapBuilder.ManagedListScreens</c>.
    /// </summary>
    public const string App = "app";

    /// <summary>Lấy từ một hệ thống khác — bắt buộc kèm <see cref="EntityFieldNote.SourceSystem"/>.</summary>
    public const string External = "external";

    /// <summary>
    /// Giá trị lạ ⇒ rỗng, tức "CHƯA CHỐT" — không phải một mặc định đoán bừa. Đoán "app tự quản lý" cho một
    /// ô còn trống là âm thầm đẻ thêm một màn hình vào phạm vi dự án; đoán "nhập tại chỗ" là khai rằng danh
    /// sách chỉ có vài giá trị mà chưa ai nói thế. Rỗng thì hai bản kể gọi tên nó ra và BA hỏi nốt.
    /// </summary>
    public static string Normalize(string? value) => value switch
    {
        Inline => Inline,
        App => App,
        External => External,
        _ => ""
    };
}

/// <summary>
/// MỘT trạng thái trong vòng đời của một đối tượng, kèm điều kiện chuyển vào nó.
///
/// <para>
/// Mỗi trạng thái ở đây là MỘT DÒNG của bảng thông báo (<see cref="NotificationMapRow"/>) — đó là chỗ
/// "ai được báo" được chốt, và nó cố tình nằm ở một bảng RIÊNG ĐỨNG SAU chứ không phải một ô cạnh trạng
/// thái này: người nhận thật gần như luôn là một QUAN HỆ với bản ghi ("người gửi đơn", "sếp của người
/// đó"), mà muốn bày ra một danh sách quan hệ/vai trò đóng thì phải có bảng phân quyền đã chốt trước.
/// </para>
/// </summary>
public class EntityLifecycleState
{
    /// <summary>Tên trạng thái đúng như người dùng gọi ("Chờ duyệt", "Đã duyệt", "Đã hủy").</summary>
    public string State { get; set; } = "";

    /// <summary>Điều kiện/hành động đưa đối tượng vào trạng thái này ("HOD bấm duyệt").</summary>
    public string EntryCondition { get; set; } = "";
}

/// <summary>
/// MỘT dòng của "bảng đối tượng nghiệp vụ": một thứ có hồ sơ riêng trong ứng dụng, các thông tin cần lưu
/// về nó, và vòng đời trạng thái nó đi qua — vòng đời ấy còn là nguồn DÒNG của bảng thông báo ngay sau đó.
///
/// <para>
/// Vì sao bảng này đứng SAU bảng luồng và bảng màn hình, chứ không mở đầu chuỗi như trực giác mách bảo:
/// người dùng nghiệp vụ kể được QUY TRÌNH, họ không kể được đối tượng nào có thông tin nào. Vòng đời trạng
/// thái lại càng phụ thuộc luồng — chuẩn <c>[RÕ]</c> của nhóm «Vòng đời &amp; trạng thái» đòi gọi tên các
/// trạng thái VÀ điều kiện chuyển, mà điều kiện chuyển chính là "ai làm bước nào", tức đầu ra của bảng
/// luồng. Hỏi ngược thứ tự là bày ra một cái bảng mà chính BA cũng chưa đủ dữ kiện để điền sẵn, và một
/// bảng điền sẵn kém thì người dùng đọc lướt rồi gật.
/// </para>
///
/// <para>
/// Phần lớn thông tin đắt giá của bảng này đã được chốt ở BẢNG CỘT nếu người dùng có gửi file — nên bản
/// chuẩn hoá không được phép hỏi lại chúng: <c>EntityMapBuilder</c> đánh dấu các thông tin trùng với cột đã
/// tích là đã có bằng chứng. Bắt người dùng duyệt lại đúng thứ họ vừa tự tay tích là hình dạng vòng lặp
/// câu hỏi chết mà <c>CoverageDeadQuestionLoopTests</c> đã phải dựng lưới một lần.
/// </para>
///
/// <para>
/// Đường tiêu thụ: mục <c>## 8. Data Model Summary</c> của AI Design Spec — hiện là mục mà bước sinh spec
/// TỰ NGHĨ RA từ văn xuôi của Product Brief, không có gì để đối chiếu.
/// </para>
/// </summary>
public class EntityMapRow
{
    /// <summary>Tên đối tượng theo ngôn ngữ nghiệp vụ ("Kế hoạch đào tạo", "Đơn đăng ký").</summary>
    public string Entity { get; set; } = "";

    /// <summary>
    /// Một câu mô tả đối tượng LÀ GÌ, BA điền sẵn — không nói AI LÀM GÌ với nó.
    ///
    /// <para>
    /// Ô này là văn xuôi của BA, không phải quyết định của người dùng: họ đọc nó như cái nhãn xám cạnh tên
    /// đối tượng rồi bấm gửi. Vì vậy nó KHÔNG đi vào bản kể gửi lên khung chat và đứng ở một dòng có gắn
    /// xuất xứ trong khối ngữ cảnh — xem ghi chú class của <c>EntityMapBuilder</c> cho ca thật: một câu mô
    /// tả nhét cả luồng duyệt vào đây đã quay lại thành "lời người dùng" và khóa cổng "Write Requirement".
    /// </para>
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>Các thông tin cần lưu về đối tượng.</summary>
    public List<EntityFieldNote> Fields { get; set; } = new();

    /// <summary>
    /// Vòng đời trạng thái. Rỗng là hợp lệ và có nghĩa RIÊNG: đối tượng danh mục (phòng ban, khóa học) chỉ
    /// tồn tại chứ không đi qua trạng thái nào. Đừng bịa ra một vòng đời cho chúng.
    /// </summary>
    public List<EntityLifecycleState> States { get; set; } = new();

    /// <summary>
    /// Tên đối tượng CHA khi dòng này không phải một hồ sơ độc lập mà là <b>các dòng của</b> một đối tượng
    /// khác — "Trách nhiệm" của một bản mô tả công việc, "Dòng hàng" của một đơn hàng, "Mục tiêu" của một
    /// bản đánh giá. Rỗng = hồ sơ độc lập, và đó là ca thường gặp nhất.
    ///
    /// <para>
    /// <b>Vì sao quan hệ này phải nằm ở bảng chứ không suy ra được ở bước sau.</b> Một "thông tin" mà thực
    /// ra là một danh sách nhiều dòng (5 trách nhiệm, mỗi dòng kèm tỷ trọng %) không có chỗ nào trong
    /// <see cref="EntityFieldNote"/> để đứng: ô đó chở đúng MỘT giá trị. Không có ô này thì BA hoặc nhét cả
    /// danh sách vào một ô text — mọi ràng buộc trên từng dòng biến mất — hoặc tách nó thành một đối tượng
    /// riêng, và khi đó <c>## 8. Data Model Summary</c> dựng nó thành một hồ sơ độc lập có màn hình CRUD
    /// riêng, sai hẳn thứ người dùng mô tả.
    /// </para>
    ///
    /// <para>
    /// <b>Vì sao là một Ô PHẲNG chứ không phải một bảng lồng trong ô "thông tin".</b> Các cột của dòng con
    /// cần đúng bộ ràng buộc mà một thông tin đã có — bắt buộc, kiểu nhập, nguồn danh sách (một cột con rất
    /// hay là dropdown lấy từ danh mục ứng dụng tự quản lý). Dựng chúng thành một cấu trúc lồng là nhân đôi
    /// cả hai trục xuống một tầng thứ ba; để dòng con LÀ một đối tượng thì mọi thứ đó dùng lại nguyên vẹn,
    /// và bảng không mọc thêm tầng nào.
    /// </para>
    ///
    /// <para>
    /// Ba chốt chặn ở <c>EntityMapBuilder</c>: tên phải khớp một dòng KHÁC trong chính bảng (không khớp ⇒
    /// hạ về hồ sơ độc lập, không nuốt dòng); không tự làm cha của mình; và <b>tối đa MỘT cấp</b> — cha của
    /// một dòng con thì không được có cha nữa. Cấm sâu hơn vì POC dựng grid lồng grid là thứ không ai duyệt
    /// nổi, và luật ấy làm mọi chu trình tự vỡ.
    /// </para>
    /// </summary>
    public string ParentEntity { get; set; } = "";

    /// <summary>
    /// Số dòng con TỐI THIỂU mỗi bản ghi cha ("mỗi JD có 5 trách nhiệm" ⇒ 5). null = không ràng buộc, và
    /// chỉ có nghĩa khi <see cref="ParentEntity"/> khác rỗng.
    ///
    /// <para>
    /// Đây là ràng buộc DUY NHẤT của tập dòng con được cấp một ô riêng, vì nó là ràng buộc duy nhất có cùng
    /// một hình dạng ở mọi dự án. Những ràng buộc khác — "tổng tỷ trọng phải bằng 100%", "luôn có sẵn một
    /// dòng mặc định không xóa được" — không có hình dạng chung (app khác là "tổng ≤ ngân sách", app khác
    /// nữa không ràng buộc gì), nên chúng thuộc về HỘI THOẠI và đi ra ở <c>## 10. Business Rules</c> qua
    /// nhóm «Quy tắc nghiệp vụ &amp; ràng buộc» của bản đồ bao phủ. Cấp cho mỗi ràng buộc một ô là đẻ ra
    /// một ô rác cho mọi dự án không cần tới nó.
    /// </para>
    /// </summary>
    public int? MinRows { get; set; }

    /// <summary>Số dòng con TỐI ĐA mỗi bản ghi cha. null = không ràng buộc. Xem <see cref="MinRows"/>.</summary>
    public int? MaxRows { get; set; }

    /// <summary>Đối tượng này có thuộc ứng dụng không. BA tích sẵn; người dùng bỏ tích thứ không cần.</summary>
    public bool Included { get; set; } = true;

    /// <summary>Dòng có BẰNG CHỨNG trong hội thoại ⇒ khóa. Cùng luật với các bảng khác.</summary>
    public bool Locked { get; set; }

    /// <summary>Trích dẫn ngắn từ hội thoại — chỉ có nghĩa khi <see cref="Locked"/>.</summary>
    public string Evidence { get; set; } = "";

    /// <summary>
    /// Đối tượng này do CHÍNH NGƯỜI DÙNG thêm vào bảng bằng nút "thêm đối tượng", không phải do BA đề xuất
    /// — cùng cờ và cùng luật với <see cref="ScreenScopeRow.AddedByUser"/>.
    ///
    /// <para>
    /// Cờ chỉ được đọc ở đường GỬI (<c>EntityMapBuilder.Sanitize</c>). Ở lượt BÀY BẢNG mọi dòng đều do model
    /// soạn, nên đọc cờ ở đó là dựng cho model một cửa sau đi vòng các chốt chặn bằng cách tự khai là người
    /// dùng.
    /// </para>
    ///
    /// <para>
    /// Nó làm hai việc, và cả hai đều là chỗ chốt chặn của bảng phải NHƯỜNG cho người dùng. Một: đối tượng
    /// "rỗng ruột" (không thông tin, không trạng thái) bị loại vì đó là một danh từ model nhặt trong hội
    /// thoại — nhưng một dòng người dùng vừa tự gõ tên thì là một câu nói *"ứng dụng còn phải lưu thứ này"*,
    /// và nuốt nó đi là bắt họ gõ lại vào khung chat đúng thứ vừa gõ trên bảng. Hai:
    /// <c>EntityMapBuilder.RenderUserMessage</c> phải GỌI TÊN chúng, vì một đối tượng chưa từng có trong đề
    /// xuất mà lặng lẽ đi vào mô hình dữ liệu là đúng loại thay đổi phải nói ra — cùng luật với các dòng bị
    /// bỏ tích. Vì vậy cờ được GIỮ qua đường lưu chứ không xoá như <see cref="Locked"/>.
    /// </para>
    /// </summary>
    public bool AddedByUser { get; set; }
}
