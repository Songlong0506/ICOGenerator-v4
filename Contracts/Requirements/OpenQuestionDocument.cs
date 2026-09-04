using System.ComponentModel;

namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Hình dạng JSON của "Điểm cần làm rõ" — danh sách CÂU HỎI của cuộc phỏng vấn. Vừa là một nửa schema
/// structured output của lượt chắt lọc bản đồ (<see cref="CoverageDistillDocument"/>), vừa là format
/// LƯU TRỮ trên <c>Project.OpenQuestions</c>. Cùng pattern với <see cref="CoverageMapDocument"/>.
/// <para>
/// <b>Đây là nguồn DUY NHẤT của "câu hỏi kế tiếp".</b> Bản đồ bao phủ chỉ chở TRẠNG THÁI của 12 nhóm
/// (<see cref="CoverageMapEntry.Status"/> + <see cref="CoverageMapEntry.Known"/> + bằng chứng); mọi câu
/// hỏi còn phải hỏi nằm ở đây. Trước đây câu hỏi tồn tại ở HAI chỗ — trường <c>nextQuestion</c> của dòng
/// bản đồ và danh sách này — do hai lời gọi LLM khác nhau chắt ra từ cùng một hội thoại; chúng nói ngược
/// nhau mà không tầng nào biết, và cả một guard (<c>CoveragePendingGuard</c>) sinh ra chỉ để hoà giải.
/// Nay cả hai ra từ MỘT lời gọi (<see cref="CoverageDistillDocument"/>) nên không còn hai giám khảo.
/// </para>
/// <para>
/// <b>Một nhóm được phép có NHIỀU câu hỏi.</b> Ô <c>nextQuestion</c> cũ chỉ chứa được một câu, nên prompt
/// phải dặn "nhiều mục cùng nhóm thì gộp thành MỘT câu" — đúng hình dạng câu hỏi kép mà
/// <c>requirement-chat.v4.md</c> cấm ở phía chat: người dùng trả lời vế đầu, các vế sau rơi mất. Danh
/// sách phẳng có nhóm là một TRƯỜNG thì mỗi điểm còn treo giữ được câu hỏi của riêng nó.
/// </para>
/// <para>
/// <b>Mục đã trả lời KHÔNG bị xoá mà được ĐÁNH DẤU.</b> Xem <see cref="OpenQuestionEntry.Status"/>.
/// </para>
/// <para>
/// Tên thuộc tính để ASCII có chủ đích, cùng lý do với <see cref="CoverageMapDocument"/>: schema này được
/// gửi thẳng cho model qua <c>response_format: json_schema</c>.
/// </para>
/// </summary>
public class OpenQuestionDocument
{
    [Description("Các câu hỏi của cuộc phỏng vấn: điểm còn mơ hồ/mâu thuẫn chưa chốt, kèm cả các mục đã được trả lời (đánh dấu ĐÃ TRẢ LỜI). Chưa có gì ⇒ mảng rỗng.")]
    public List<OpenQuestionEntry> Items { get; set; } = new();
}

/// <summary>Một câu hỏi của cuộc phỏng vấn trong <see cref="OpenQuestionDocument"/>.</summary>
public class OpenQuestionEntry
{
    /// <summary>Trạng thái của một mục CÒN PHẢI HỎI.</summary>
    public const string Open = "MỞ";

    /// <summary>Trạng thái của một mục đã có câu trả lời trong hội thoại/tài liệu/bảng đã chốt.</summary>
    public const string Answered = "ĐÃ TRẢ LỜI";

    [Description("Chép ĐÚNG MỘT nhãn nhóm của bản đồ bao phủ mà câu hỏi này thuộc về. Không thuộc nhóm nào thì để rỗng.")]
    public string Group { get; set; } = string.Empty;

    [Description("CÂU HỎI hoàn chỉnh sẽ được bày NGUYÊN VĂN cho người dùng — không phải câu mô tả trạng thái. KHÔNG chép tên nhóm vào đây.")]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// <c>MỞ</c> (còn phải hỏi) hay <c>ĐÃ TRẢ LỜI</c>. Mục đã trả lời **ở lại danh sách** thay vì bị xoá:
    /// lượt chắt lọc được đính chính danh sách cũ, nên một mục biến mất là một mục nó có thể sinh lại ở
    /// lượt sau — đúng vòng lặp đã đốt ba lượt của buổi <i>JD Libary 5</i>. Đánh dấu thì mục ấy vừa đứng
    /// ngoài mọi đường hỏi, vừa còn nguyên trong khối echo để không ai dựng lại nó.
    /// <para>
    /// Giá trị lạ ⇒ chuẩn hoá về <see cref="Open"/> (fail-open): đọc hụt một trạng thái chỉ tốn thêm một
    /// câu hỏi, còn đọc nhầm thành "đã trả lời" là im lặng bỏ mất một điểm chưa ai chốt.
    /// </para>
    /// </summary>
    [Description("Một trong: MỞ | ĐÃ TRẢ LỜI. ĐÃ TRẢ LỜI khi hội thoại/tài liệu/bảng đã chốt đã trả lời xong câu này.")]
    public string Status { get; set; } = Open;

    /// <summary>
    /// Câu trả lời đã thu được — trích NGẮN, nguyên văn, để người dùng rà lại được vì sao một câu bị đóng.
    /// Cùng vai trò với <see cref="CoverageMapEntry.Evidence"/> của dòng bản đồ.
    /// </summary>
    [Description("Trích NGẮN nguyên văn câu trả lời đã thu được. Bắt buộc khi ĐÃ TRẢ LỜI; rỗng khi MỞ.")]
    public string Answer { get; set; } = string.Empty;

    /// <summary>Mục này còn phải hỏi hay không — phép thử dùng chung của mọi tầng đọc.</summary>
    public bool IsOpen => !string.Equals(Status, Answered, StringComparison.Ordinal);
}
