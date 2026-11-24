using ICOGenerator.Localization;

namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Loại của một luồng trong bảng luồng nghiệp vụ. Giá trị là MÃ không phụ thuộc ngôn ngữ — đi vào JSON
/// của <c>Project.FlowMap</c>, vào payload của trình duyệt và là thứ model phải trả về
/// (<c>table-flow-map.v1.md</c>). Chữ người dùng đọc lấy qua <see cref="Label"/>.
/// </summary>
public static class FlowKind
{
    /// <summary>Đường đi thuận — việc chạy đúng như mong đợi.</summary>
    public const string Happy = "main";

    /// <summary>Một tình huống hỏng cụ thể và cách xử lý nó (từ chối, quá hạn, trùng, thiếu điều kiện…).</summary>
    public const string Exception = "exception";

    /// <summary>Hai loại hợp lệ, theo thứ tự bày trên bảng.</summary>
    public static readonly string[] All = { Happy, Exception };

    /// <summary>Nhãn của loại luồng theo ngôn ngữ đang dùng.</summary>
    public static string Label(string? kind) => Loc.T("FlowKind.Label." + Normalize(kind));

    /// <summary>
    /// Kéo mọi cách viết của model về đúng hai mã. Prompt đòi đúng mã, nên đường chính là so nguyên văn;
    /// phần còn lại đỡ cho model viết NHÃN thay cho mã ("ngoại lệ", "Exception flow") — so với nhãn và
    /// các cụm gợi ý của loại ngoại lệ ở mọi ngôn ngữ (resource <c>FlowKind.ExceptionCues</c>). Không nhận
    /// ra ⇒ coi là luồng chính.
    /// </summary>
    public static string Normalize(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0 || value == Happy)
            return Happy;

        return value.Contains(Exception, StringComparison.Ordinal) || ExceptionCues().Any(cue => value.Contains(cue, StringComparison.Ordinal))
            ? Exception
            : Happy;
    }

    private static IEnumerable<string> ExceptionCues()
        => Loc.InAllLanguages("FlowKind.ExceptionCues").SelectMany(LocList.Split)
            .Concat(Loc.InAllLanguages("FlowKind.Label." + Exception).Select(x => x.ToLowerInvariant()));
}

/// <summary>
/// MỘT bước của một luồng: ai làm, làm gì, sau đó hệ thống ở trạng thái nào.
///
/// <para>
/// Bước ở đây SỬA ĐƯỢC và BỎ ĐƯỢC — chỗ khác quyết định so với sơ đồ luồng chỉ-đọc từng vẽ ở lượt mời
/// "Write Requirement" (đã gỡ). Sơ đồ cũ là một bức tranh để người dùng nói "chưa đúng" bằng lời
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
    /// Bước này do CHÍNH NGƯỜI DÙNG thêm vào bảng bằng nút "+ thêm bước", không phải do BA đề xuất.
    ///
    /// <para>
    /// Bảng luồng KHÔNG có chốt chặn kiểu "màn hình bịa" để cờ phải đi vòng qua (bước là chữ tự do, không
    /// phải khóa nối sang bảng nào), nên ở đây cờ chỉ còn đúng một việc — và đó là việc bắt buộc: một bước
    /// chưa từng có trong đề xuất mà lặng lẽ đi vào phạm vi là đúng loại thay đổi phải NÓI RA, cùng luật
    /// với các bước bị bỏ. Nói ra ở đâu: <see cref="ICOGenerator.Services.Requirements.FlowMapBuilder.RenderUserMessage"/>.
    /// Việc phải nói ra ở bảng này còn nặng hơn các bảng khác, vì mỗi bước được giữ là một mục
    /// <c>IncludedActions</c> mà bản thiết kế UX sau đó BẮT BUỘC phải có tác vụ phụ trách
    /// (<c>UxInventoryBuilder.UncoveredSteps</c>) — thêm một bước ở đây là mở lại lượt thiết kế ở lượt sau.
    /// </para>
    ///
    /// <para>
    /// Chỉ có nghĩa ở đường GỬI (<see cref="ICOGenerator.Services.Requirements.FlowMapBuilder.Sanitize"/>).
    /// Đường BÀY BẢNG ép cờ về <c>false</c>: lượt đó là đề xuất của model, một cờ "người dùng tự thêm" ở
    /// đấy chỉ là chỗ để model gán chữ ký của người dùng lên thứ chính nó vừa bịa.
    /// </para>
    /// </summary>
    public bool AddedByUser { get; set; }

    /// <summary>
    /// Bước này được làm NGOÀI PHẦN MỀM (gọi điện, xuống kiểm tra tận nơi, một bộ phận khác gửi file qua
    /// email…) — ứng dụng không có màn hình hay chức năng nào cho nó, và đó là chủ ý chứ không phải lỗ hổng.
    ///
    /// <para>
    /// Cờ do BA đặt ở lượt DANH MỤC TÁC VỤ của bản thiết kế UX (<c>UxDesignService</c>), SAU khi người dùng
    /// đã chốt bảng luồng: bước vẫn là một phần của câu chuyện nghiệp vụ nên không bị bỏ khỏi luồng, nhưng
    /// phép kiểm mối nối luồng ⇄ tác vụ (<c>UxInventoryBuilder.UncoveredSteps</c>) thôi đòi một tác vụ phụ
    /// trách nó. Trước khi có cờ này, đúng loại bước ấy là thứ bị hỏi ngược người dùng "bước này làm ngoài
    /// phần mềm hay còn thiếu màn hình?" — một quyết định thiết kế mà họ đi thuê BA để ra.
    /// </para>
    ///
    /// <para>
    /// Không phải chữ ký của người dùng: đó là quyết định thiết kế của BA, giống mọi phần khác của bản thiết
    /// kế UX. Đường BÀY/GỬI bảng luồng không chở cờ này — nó chỉ sống trên bảng đã lưu.
    /// </para>
    /// </summary>
    public bool OutsideSoftware { get; set; }

    /// <summary>Một câu của BA nói vì sao bước làm ngoài phần mềm. Chỉ có nghĩa khi <see cref="OutsideSoftware"/> bật.</summary>
    public string OutsideReason { get; set; } = "";
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
/// Đây cũng là đường DUY NHẤT để luồng người dùng vừa gật đi tới POC: mỗi bước của bảng đã chốt thành một
/// chuyển trạng thái ở mục <c>## 8. Business Rules</c> của AI Design Spec, tức POC dựng theo đúng chuỗi
/// bước người dùng tự tay duyệt thay vì theo bản LLM chắt từ transcript.
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
    /// Nó đi thẳng vào khối "bảng đã chốt" của mọi lượt chat sau đó và vào các chuyển trạng thái ở
    /// <c>## 8. Business Rules</c> của spec, tức POC dựng theo đúng thứ tự này. Vì vậy người dùng đổi được thứ tự ngay trên bảng
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
    /// Tên luồng KHÔNG phải khóa nối sang bảng nào (khác ID tác vụ của bản thiết kế UX, thứ là khóa của bảng
    /// phân quyền), nên một luồng tự thêm không cần đường lách chốt chặn nào — nó chỉ cần được GỌI TÊN
    /// trong tin nhắn gửi vào hội thoại, vì một luồng chưa từng có trong đề xuất mà lặng lẽ trở thành ví dụ
    /// chấm POC là thứ phải nói ra.
    /// </para>
    /// </summary>
    public bool AddedByUser { get; set; }
}
