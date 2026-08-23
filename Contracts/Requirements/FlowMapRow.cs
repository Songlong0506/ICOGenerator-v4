namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Loại của một luồng trong bảng luồng nghiệp vụ. Lưu dưới dạng CHUỖI (không phải enum) vì cùng lý do với
/// <see cref="PermissionScope"/>: chuỗi đã nằm trong JSON của <c>Project.FlowMap</c> và của hội thoại, đổi
/// tên giá trị là làm hỏng dữ liệu cũ.
/// </summary>
public static class FlowKind
{
    /// <summary>Đường đi thuận — việc chạy đúng như mong đợi.</summary>
    public const string Happy = "luồng chính";

    /// <summary>Một tình huống hỏng cụ thể và cách xử lý nó (từ chối, quá hạn, trùng, thiếu điều kiện…).</summary>
    public const string Exception = "ngoại lệ";

    /// <summary>Hai loại hợp lệ, theo thứ tự bày trên bảng.</summary>
    public static readonly string[] All = { Happy, Exception };

    /// <summary>Kéo mọi cách viết của model về đúng hai loại. Không nhận ra ⇒ coi là luồng chính.</summary>
    public static string Normalize(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
            return Happy;

        return value.Contains("ngoại lệ", StringComparison.Ordinal)
               || value.Contains("ngoai le", StringComparison.Ordinal)
               || value.Contains("exception", StringComparison.Ordinal)
               || value.Contains("lỗi", StringComparison.Ordinal)
               || value.Contains("từ chối", StringComparison.Ordinal)
            ? Exception
            : Happy;
    }
}

/// <summary>
/// MỘT bước của một luồng: ai làm, làm gì, sau đó hệ thống ở trạng thái nào.
///
/// <para>
/// Khác <see cref="FlowStep"/> (sơ đồ luồng chỉ-đọc vẽ ở lượt mời "Write Requirement") đúng ở chỗ quyết
/// định: bước ở đây SỬA ĐƯỢC và BỎ ĐƯỢC. Sơ đồ cũ là một bức tranh để người dùng nói "chưa đúng" bằng lời
/// trong khung chat rồi chờ BA vẽ lại; ở đây họ sửa thẳng vào bước sai. Đó là khác biệt giữa "một cơ hội
/// phản đối" và "một thao tác trên từng bước" — cùng thứ đã tách bảng phân quyền khỏi chip "Đồng ý".
/// </para>
/// </summary>
public class FlowMapStep
{
    /// <summary>Vai trò thực hiện bước, đúng như người dùng gọi ("Nhân viên", "HOD", "Hệ thống").</summary>
    public string Actor { get; set; } = "";

    /// <summary>Hành động ở bước này ("Gửi đơn đăng ký").</summary>
    public string Action { get; set; } = "";

    /// <summary>Trạng thái/kết quả sau bước ("Đơn ở trạng thái Chờ duyệt"). Rỗng nếu bước không đổi trạng thái.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>
    /// Bước này có đúng không. BA để MỌI bước nó đề xuất ở trạng thái được giữ; người dùng bấm <b>×</b> để
    /// bỏ bước sai. Bỏ chứ không xóa hẳn khỏi dữ liệu để lượt gửi còn kể lại được rằng bước đó đã bị loại —
    /// im lặng bỏ đi thì người dùng không có bằng chứng nào cho thấy mình vừa loại đúng thứ định loại (cùng
    /// luật với bảng cột).
    ///
    /// <para>
    /// Bảng này KHÔNG có cờ <c>locked</c>/<c>evidence</c> như bảng phân quyền, vì cùng lý do đã bỏ chúng ở
    /// bảng màn hình: mọi bước ra khỏi builder đều ở trạng thái được giữ, nên một trích dẫn ở đây không đổi
    /// được trạng thái nào — nó chỉ khóa cứng cột đầu và biến cả bảng thành chỉ-đọc ở đúng chiều mà người
    /// dùng cần bác. Xem <c>docs/requirement-flow.md</c>, mục "Vì sao bảng luồng và bảng màn hình không có
    /// dấu ✓ bằng chứng".
    /// </para>
    /// </summary>
    public bool Included { get; set; } = true;

    /// <summary>
    /// Bước này do CHÍNH NGƯỜI DÙNG thêm vào bảng bằng nút "+ thêm bước", không phải do BA đề xuất — cùng
    /// cờ và cùng luật với <see cref="ScreenScopeRow.AddedByUser"/>.
    ///
    /// <para>
    /// Bảng luồng KHÔNG có chốt chặn kiểu "màn hình bịa" để cờ phải đi vòng qua (bước là chữ tự do, không
    /// phải khóa nối sang bảng nào), nên ở đây cờ chỉ còn đúng một việc — và đó là việc bắt buộc: một bước
    /// chưa từng có trong đề xuất mà lặng lẽ đi vào phạm vi là đúng loại thay đổi phải NÓI RA, cùng luật
    /// với các bước bị bỏ. Nói ra ở đâu: <see cref="ICOGenerator.Services.Requirements.FlowMapBuilder.RenderUserMessage"/>.
    /// Việc phải nói ra ở bảng này còn nặng hơn các bảng khác, vì mỗi bước được giữ là một mục
    /// <c>IncludedActions</c> mà bảng màn hình sau đó BẮT BUỘC phải có chức năng phụ trách
    /// (<c>ScreenScopeMapBuilder.UncoveredActions</c>) — thêm một bước ở đây là siết một cổng ở lượt sau.
    /// </para>
    ///
    /// <para>
    /// Chỉ có nghĩa ở đường GỬI (<see cref="ICOGenerator.Services.Requirements.FlowMapBuilder.Sanitize"/>).
    /// Đường BÀY BẢNG ép cờ về <c>false</c>: lượt đó là đề xuất của model, một cờ "người dùng tự thêm" ở
    /// đấy chỉ là chỗ để model gán chữ ký của người dùng lên thứ chính nó vừa bịa.
    /// </para>
    /// </summary>
    public bool AddedByUser { get; set; }
}

/// <summary>
/// MỘT luồng nghiệp vụ của ứng dụng: một chuỗi bước có tên, gắn với vai trò khởi xướng và loại
/// (<see cref="FlowKind"/>).
///
/// <para>
/// Vì sao luồng được chốt bằng BẢNG chứ không bằng hội thoại: luồng vốn ĐÃ được hỏi trong chat và nhóm
/// «Chức năng &amp; luồng nghiệp vụ chính» hoàn toàn có thể lên <c>[RÕ]</c> từ vài câu trả lời rời rạc — nhưng
/// thứ đi tiếp vào tài liệu là bản BA tự ráp lại các câu đó thành một chuỗi, và người dùng chưa bao giờ
/// nhìn thấy chuỗi ấy để bác. Sơ đồ luồng ở lượt mời tạo tài liệu có nhìn thấy, nhưng nó tới quá muộn (ngay
/// trước nút) và chỉ vẽ được MỘT luồng chính, không có ngoại lệ.
/// </para>
///
/// <para>
/// Vì sao bảng phải chở NGOẠI LỆ: chuẩn <c>[RÕ]</c> của nhóm «Luồng ngoại lệ» đòi một tình huống hỏng cụ
/// thể kèm cách xử lý, và đó là loại thông tin không bao giờ tự nhiên xuất hiện trong một buổi phỏng vấn —
/// người dùng kể đường đi thuận, còn đường hỏng thì họ coi là hiển nhiên. Một hoặc hai ngoại lệ đặt cạnh
/// luồng chính là chỗ rẻ nhất để hỏi chúng.
/// </para>
///
/// <para>
/// Đây cũng là đường DUY NHẤT để luồng người dùng vừa gật đi tới oracle chấm POC: bảng đã chốt được dựng
/// thành các ví dụ ĐỊNH TÍNH của mục <c>## 13. Worked Examples</c> trong AI Design Spec, tức POC bị chấm
/// theo đúng chuỗi bước người dùng tự tay duyệt thay vì theo bản LLM chắt từ transcript.
/// </para>
/// </summary>
public class FlowMapRow
{
    /// <summary>Tên luồng theo ngôn ngữ nghiệp vụ ("Đăng ký khóa học", "Duyệt kế hoạch quý").</summary>
    public string Name { get; set; } = "";

    /// <summary>Một trong các hằng số <see cref="FlowKind"/>.</summary>
    public string Kind { get; set; } = FlowKind.Happy;

    /// <summary>Vai trò khởi xướng luồng — cột "theo vai trò" của bảng. Rỗng nếu luồng do hệ thống tự chạy.</summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// Điều kiện kích hoạt của một luồng NGOẠI LỆ ("người duyệt từ chối", "quá hạn đăng ký"). Rỗng với
    /// luồng chính — luồng chính không có điều kiện kích hoạt nào ngoài chính việc người dùng bắt đầu nó.
    /// </summary>
    public string Trigger { get; set; } = "";

    /// <summary>
    /// Các bước theo đúng THỨ TỰ XẢY RA — và ở bảng này thứ tự là dữ liệu, không phải cách bày.
    ///
    /// <para>
    /// Nó đi thẳng vào khối "bảng đã chốt" của mọi lượt chat sau đó và vào <c>## 13. Worked Examples</c>,
    /// tức oracle chấm POC bị chấm theo đúng thứ tự này. Vì vậy người dùng đổi được thứ tự ngay trên bảng
    /// (nút ↑ ↓ ở cuối mỗi dòng): BA ráp sai thứ tự mà chỉ sửa được bằng cách gõ đè chữ của hai dòng là
    /// một đường sửa đắt tới mức không ai đi, và cái sai thì đi tiếp vào tài liệu.
    /// </para>
    /// </summary>
    public List<FlowMapStep> Steps { get; set; } = new();

    /// <summary>
    /// Luồng này do CHÍNH NGƯỜI DÙNG thêm vào bảng bằng nút "+ thêm luồng" — cùng cờ và cùng luật với
    /// <see cref="FlowMapStep.AddedByUser"/>.
    ///
    /// <para>
    /// Tên luồng KHÔNG phải khóa nối sang bảng nào (khác <see cref="ScreenScopeRow.Screen"/>, thứ là khóa ở
    /// bốn chỗ độc lập), nên một luồng tự thêm không cần đường lách chốt chặn nào — nó chỉ cần được GỌI TÊN
    /// trong tin nhắn gửi vào hội thoại, vì một luồng chưa từng có trong đề xuất mà lặng lẽ trở thành ví dụ
    /// chấm POC là thứ phải nói ra.
    /// </para>
    /// </summary>
    public bool AddedByUser { get; set; }
}
