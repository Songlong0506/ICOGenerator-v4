namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Một dòng của "Bản đồ bao phủ yêu cầu": nhóm thông tin, trạng thái khai thác
/// ([RÕ]/[MỘT PHẦN]/[CHƯA HỎI]/[KHÔNG ÁP DỤNG]), phần đã ghi nhận, CÂU HỎI kế tiếp, và bằng chứng.
/// <para>
/// <b>Bốn trường, không phải một chuỗi.</b> Bản đồ từng được lưu dưới dạng một dòng text nhồi cả bốn thứ
/// vào nhau — <c>- ★ Nhãn: [TRẠNG THÁI] đã ghi nhận còn thiếu: phần hụt {nguồn: trích dẫn}</c> — nên mọi
/// tầng muốn sửa MỘT phần đều phải regex ra rồi ghép chuỗi lại: bốn guard
/// (<c>CoveragePendingGuard</c>, <c>CoverageStaleGapGuard</c>, <c>CoverageWorkedExampleGuard</c>,
/// <c>CoverageConfirmedTableGuard</c>) đều làm đúng việc đó, mỗi cái một kiểu, và mỗi cái phải tự nhớ
/// dựng lại cờ ★ với khối <c>{nguồn: …}</c> cho đúng. Tách thành trường bậc nhất thì các guard chỉ còn
/// gán thuộc tính, và <see cref="NextQuestion"/> — thứ mà <c>RequirementReadinessGate</c> lấy làm câu chặn
/// — không còn phải bóc bằng <c>IndexOf("còn thiếu:")</c> trên chuỗi tóm tắt.
/// </para>
/// </summary>
public class CoverageMapItem
{
    /// <summary>
    /// Dấu ngăn giữa phần đã ghi nhận và câu hỏi kế tiếp khi hai trường được GHÉP LẠI thành một dòng cho
    /// người đọc — <see cref="Summary"/> (panel tiến độ) và <c>CoverageMapParser.ToText</c> (ngữ cảnh chat
    /// của BA, bản xuất hội thoại). Chiều lưu trữ không dùng tới nó: ở đó <see cref="Known"/> và
    /// <see cref="NextQuestion"/> là hai ô riêng. Chuỗi giữ nguyên chữ "còn thiếu:" dù trường đã đổi tên:
    /// đây là thứ NGƯỜI DÙNG đọc trên panel và model đọc trong ngữ cảnh chat, đổi nó không sửa được gì mà
    /// làm lệch mọi bản đồ text đang nằm trong hội thoại cũ.
    /// </summary>
    public const string NextQuestionMarker = "còn thiếu:";

    public string Label { get; set; } = string.Empty;

    /// <summary>Trạng thái đã chuẩn hoá: "RÕ" | "MỘT PHẦN" | "CHƯA HỎI" | "KHÔNG ÁP DỤNG".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Phần ĐÃ ghi nhận của nhóm này — điều bản đồ coi như đã biết. Trước đây là "phần đầu của tóm tắt,
    /// tính tới chữ 'còn thiếu:'"; nay là một trường riêng nên không tầng nào phải cắt chuỗi nữa.
    /// </summary>
    public string Known { get; set; } = string.Empty;

    /// <summary>
    /// CÂU HỎI KẾ TIẾP của nhóm này — thứ <c>RequirementReadinessGate</c> bày NGUYÊN VĂN ra khung chat khi
    /// người dùng bấm "Write Requirement". Rỗng nghĩa là nhóm này không có câu hỏi nào chờ.
    /// <para>
    /// <b>Vì sao tên trường là câu hỏi chứ không phải "chỗ hụt".</b> Trường này từng tên <c>gap</c> —
    /// "điều còn phải hỏi" — và một cái tên như thế cho phép model ghi vào đây một câu MÔ TẢ TRẠNG THÁI
    /// hoàn toàn hợp lệ theo nghĩa của nó: <i>"Bảng thông báo theo sự kiện chưa được chốt."</i> Đúng là một
    /// chỗ hụt, nhưng cổng thì phát nguyên văn nó ra màn hình, nên người dùng nhận một câu không hỏi gì cả
    /// và BA cũng không biết phải hỏi gì. Tên trường là thứ model bị chấm theo, nên nó phải nói đúng thứ
    /// cần: một câu hỏi hoàn chỉnh, người dùng đọc xong biết phải kể điều gì.
    /// </para>
    /// <para>
    /// Đây là trường đắt nhất của cả bản đồ: một câu hỏi chết còn nằm đây là một câu người dùng đã trả lời
    /// được phát lại tới khi họ bỏ cuộc (xem <c>CoverageStaleGapGuard</c>), và một câu không trả lời được
    /// thì khoá cổng vĩnh viễn (xem <c>CoverageQuestionGuard</c>).
    /// </para>
    /// </summary>
    public string NextQuestion { get; set; } = string.Empty;

    /// <summary>
    /// BẰNG CHỨNG cho kết luận của dòng này: trích ngắn NGUYÊN VĂN điều người dùng đã nói/tài liệu đã ghi
    /// mà trạng thái dựa vào. Không có bằng chứng thì người dùng không có cách nào biết vì sao một nhóm bị
    /// chấm [RÕ] — và một nhóm bị chấm [RÕ] oan thì BA sẽ KHÔNG BAO GIỜ hỏi lại nó nữa.
    /// <para>
    /// Phải giữ NGUYÊN VĂN: người dùng rà một dòng [RÕ] bằng cách tìm lại chính câu mình đã nói. Một
    /// trích dẫn được "viết lại cho gọn" thì không còn đối chiếu được với hội thoại, và dòng đó mất
    /// đường kiểm chứng duy nhất của nó.
    /// </para>
    /// </summary>
    public string Evidence { get; set; } = string.Empty;

    public bool IsCore { get; set; }

    /// <summary>
    /// Tóm tắt gộp cho UI: phần đã ghi nhận, nối câu hỏi kế tiếp nếu có. Giữ lại đúng chuỗi mà panel
    /// "Tiến độ khai thác" (server render lẫn <c>renderCoverage()</c> trong <c>requirements.js</c>) vẫn
    /// đang hiện, nên đổi format lưu trữ không đổi một pixel nào trên màn hình.
    /// </summary>
    public string Summary => string.IsNullOrWhiteSpace(NextQuestion)
        ? Known
        : string.IsNullOrWhiteSpace(Known)
            ? $"{NextQuestionMarker} {NextQuestion}"
            : $"{Known} {NextQuestionMarker} {NextQuestion}";
}
