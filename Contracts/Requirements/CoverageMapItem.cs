namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Một dòng của "Bản đồ bao phủ yêu cầu": nhóm thông tin, trạng thái khai thác
/// ([RÕ]/[MỘT PHẦN]/[CHƯA HỎI]/[KHÔNG ÁP DỤNG]), phần đã ghi nhận, phần còn thiếu, và bằng chứng.
/// <para>
/// <b>Bốn trường, không phải một chuỗi.</b> Bản đồ từng được lưu dưới dạng một dòng text nhồi cả bốn thứ
/// vào nhau — <c>- ★ Nhãn: [TRẠNG THÁI] đã ghi nhận còn thiếu: phần hụt {nguồn: trích dẫn}</c> — nên mọi
/// tầng muốn sửa MỘT phần đều phải regex ra rồi ghép chuỗi lại: bốn guard
/// (<c>CoveragePendingGuard</c>, <c>CoverageStaleGapGuard</c>, <c>CoverageWorkedExampleGuard</c>,
/// <c>CoverageConfirmedTableGuard</c>) đều làm đúng việc đó, mỗi cái một kiểu, và mỗi cái phải tự nhớ
/// dựng lại cờ ★ với khối <c>{nguồn: …}</c> cho đúng. Tách thành trường bậc nhất thì các guard chỉ còn
/// gán thuộc tính, và <see cref="Gap"/> — thứ mà <c>RequirementReadinessGate</c> lấy làm câu chặn — không
/// còn phải bóc bằng <c>IndexOf("còn thiếu:")</c> trên chuỗi tóm tắt.
/// </para>
/// </summary>
public class CoverageMapItem
{
    /// <summary>
    /// Dấu ngăn giữa phần đã ghi nhận và phần còn thiếu khi hai trường được GHÉP LẠI thành một dòng cho
    /// người đọc — <see cref="Summary"/> (panel tiến độ) và <c>CoverageMapParser.ToText</c> (ngữ cảnh chat
    /// của BA, bản xuất hội thoại). Chiều lưu trữ không dùng tới nó: ở đó <see cref="Known"/> và
    /// <see cref="Gap"/> là hai ô riêng.
    /// </summary>
    public const string GapMarker = "còn thiếu:";

    public string Label { get; set; } = string.Empty;

    /// <summary>Trạng thái đã chuẩn hoá: "RÕ" | "MỘT PHẦN" | "CHƯA HỎI" | "KHÔNG ÁP DỤNG".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Phần ĐÃ ghi nhận của nhóm này — điều bản đồ coi như đã biết. Trước đây là "phần đầu của tóm tắt,
    /// tính tới chữ 'còn thiếu:'"; nay là một trường riêng nên không tầng nào phải cắt chuỗi nữa.
    /// </summary>
    public string Known { get; set; } = string.Empty;

    /// <summary>
    /// Phần CÒN THIẾU — điều còn phải hỏi để nhóm này lên [RÕ]. Rỗng nghĩa là không còn gì phải hỏi.
    /// <c>RequirementReadinessGate</c> lấy thẳng trường này làm câu chặn của cổng "Write Requirement",
    /// nên nó là trường đắt nhất của cả bản đồ: một mẩu chết còn nằm đây là một câu hỏi người dùng đã
    /// trả lời được phát lại tới khi họ bỏ cuộc (xem <c>CoverageStaleGapGuard</c>).
    /// </summary>
    public string Gap { get; set; } = string.Empty;

    /// <summary>
    /// BẰNG CHỨNG cho kết luận của dòng này: trích ngắn NGUYÊN VĂN điều người dùng đã nói/tài liệu đã ghi
    /// mà trạng thái dựa vào. Không có bằng chứng thì người dùng không có cách nào biết vì sao một nhóm bị
    /// chấm [RÕ] — và một nhóm bị chấm [RÕ] oan thì BA sẽ KHÔNG BAO GIỜ hỏi lại nó nữa.
    /// <para>
    /// Phải giữ NGUYÊN VĂN: <c>DecisionUnderHarvestGuard</c> khớp trích dẫn này bằng phép tìm chuỗi con
    /// trong lời người dùng của lô lượt, và đó là toàn bộ cơ chế biến bản đồ thành bộ đọc độc lập làm
    /// chứng cho nhật ký quyết định. Viết lại cho "gọn" là tắt guard đó trong im lặng.
    /// </para>
    /// </summary>
    public string Evidence { get; set; } = string.Empty;

    public bool IsCore { get; set; }

    /// <summary>
    /// Tóm tắt gộp cho UI: phần đã ghi nhận, nối mẩu còn thiếu nếu có. Giữ lại đúng chuỗi mà panel
    /// "Tiến độ khai thác" (server render lẫn <c>renderCoverage()</c> trong <c>requirements.js</c>) vẫn
    /// đang hiện, nên đổi format lưu trữ không đổi một pixel nào trên màn hình.
    /// </summary>
    public string Summary => string.IsNullOrWhiteSpace(Gap)
        ? Known
        : string.IsNullOrWhiteSpace(Known) ? $"{GapMarker} {Gap}" : $"{Known} {GapMarker} {Gap}";
}
