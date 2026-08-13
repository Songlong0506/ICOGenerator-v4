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
    /// Bước này có đúng không. BA TÍCH SẴN mọi bước nó đề xuất; người dùng bỏ tích bước sai. Bỏ tích chứ
    /// không xóa hẳn để lượt gửi còn kể lại được rằng bước đó đã bị loại — im lặng bỏ đi thì người dùng
    /// không có bằng chứng nào cho thấy mình vừa loại đúng thứ định loại (cùng luật với bảng cột).
    /// </summary>
    public bool Included { get; set; } = true;

    /// <summary>
    /// Bước này đã có BẰNG CHỨNG trong hội thoại nên được khóa lại (hiện ✓ + tooltip trích dẫn). Luật bằng
    /// chứng giống hệt <see cref="PermissionGrant.Locked"/>: server chỉ khóa khi model kèm được trích dẫn,
    /// vì một bảng điền sẵn toàn bộ và trông như đã chốt chính là cái chip "Đồng ý phương án này" phóng to.
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>Trích dẫn ngắn từ hội thoại — chỉ có nghĩa khi <see cref="Locked"/>.</summary>
    public string Evidence { get; set; } = "";
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

    /// <summary>Các bước theo đúng thứ tự xảy ra.</summary>
    public List<FlowMapStep> Steps { get; set; } = new();
}
