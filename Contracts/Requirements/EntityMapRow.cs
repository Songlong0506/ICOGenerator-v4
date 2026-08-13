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
}

/// <summary>
/// MỘT trạng thái trong vòng đời của một đối tượng, kèm điều kiện chuyển vào và AI ĐƯỢC BÁO khi nó xảy ra.
///
/// <para>
/// Cột <see cref="Notify"/> nằm ở đây chứ không ở một bảng riêng vì thông báo là thứ chỉ có nghĩa khi gắn
/// vào một chuyển trạng thái cụ thể. Hỏi nhóm «Thông báo / nhắc nhở» bằng một câu chung chung cho ra đúng
/// loại câu trả lời mà chuẩn <c>[RÕ]</c> đã phải cấm — một danh sách vai trò trần, rồi tài liệu đóng băng
/// thành "mọi thay đổi trạng thái gửi cho cả bốn nhóm", tức mỗi lần một bản kế hoạch đổi trạng thái thì
/// toàn bộ nhân viên nhà máy nhận email. Đặt ô nhận-thông-báo ngay cạnh từng trạng thái biến câu hỏi mơ hồ
/// đó thành vài ô cụ thể có sẵn ngữ cảnh.
/// </para>
/// </summary>
public class EntityLifecycleState
{
    /// <summary>Tên trạng thái đúng như người dùng gọi ("Chờ duyệt", "Đã duyệt", "Đã hủy").</summary>
    public string State { get; set; } = "";

    /// <summary>Điều kiện/hành động đưa đối tượng vào trạng thái này ("HOD bấm duyệt").</summary>
    public string EntryCondition { get; set; } = "";

    /// <summary>
    /// Ai được báo khi đối tượng vào trạng thái này. Rỗng = KHÔNG báo cho ai — một quyết định hợp lệ và
    /// phải nói ra được, vì mặc định im lặng của các tầng sau là gửi cho tất cả.
    /// </summary>
    public string Notify { get; set; } = "";
}

/// <summary>
/// MỘT dòng của "bảng đối tượng nghiệp vụ": một thứ có hồ sơ riêng trong ứng dụng, các thông tin cần lưu
/// về nó, và vòng đời trạng thái nó đi qua.
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

    /// <summary>Một câu mô tả đối tượng là gì, BA điền sẵn.</summary>
    public string Description { get; set; } = "";

    /// <summary>Các thông tin cần lưu về đối tượng.</summary>
    public List<EntityFieldNote> Fields { get; set; } = new();

    /// <summary>
    /// Vòng đời trạng thái. Rỗng là hợp lệ và có nghĩa RIÊNG: đối tượng danh mục (phòng ban, khóa học) chỉ
    /// tồn tại chứ không đi qua trạng thái nào. Đừng bịa ra một vòng đời cho chúng.
    /// </summary>
    public List<EntityLifecycleState> States { get; set; } = new();

    /// <summary>Đối tượng này có thuộc ứng dụng không. BA tích sẵn; người dùng bỏ tích thứ không cần.</summary>
    public bool Included { get; set; } = true;

    /// <summary>Dòng có BẰNG CHỨNG trong hội thoại ⇒ khóa. Cùng luật với các bảng khác.</summary>
    public bool Locked { get; set; }

    /// <summary>Trích dẫn ngắn từ hội thoại — chỉ có nghĩa khi <see cref="Locked"/>.</summary>
    public string Evidence { get; set; } = "";
}
