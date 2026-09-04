namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Một dòng của "Bản đồ bao phủ yêu cầu": nhóm thông tin, trạng thái khai thác
/// ([RÕ]/[MỘT PHẦN]/[CHƯA HỎI]/[KHÔNG ÁP DỤNG]), phần đã ghi nhận, và bằng chứng.
/// <para>
/// <b>Trường bậc nhất, không phải một chuỗi.</b> Bản đồ từng được lưu dưới dạng một dòng text nhồi cả bốn
/// thứ vào nhau — <c>- ★ Nhãn: [TRẠNG THÁI] đã ghi nhận còn thiếu: phần hụt {nguồn: trích}</c> — nên mọi
/// tầng muốn sửa MỘT phần đều phải regex ra rồi ghép chuỗi lại, và mỗi guard phải tự nhớ dựng lại cờ ★ với
/// khối <c>{nguồn: …}</c> cho đúng. Tách thành trường thì các guard chỉ còn gán thuộc tính.
/// </para>
/// <para>
/// <b>Dòng bản đồ KHÔNG chở câu hỏi.</b> Câu hỏi còn phải hỏi nằm ở <see cref="OpenQuestionDocument"/> —
/// một danh sách phẳng, nhóm là một trường, một nhóm được phép có nhiều câu. <see cref="Questions"/> dưới
/// đây là các câu hỏi MỞ của nhóm này được GẮN VÀO lúc đọc (<c>CoverageMapParser.AttachQuestions</c>) cho
/// những tầng cần nhìn cả hai thứ một lúc; nó không nằm trong JSON đã lưu.
/// </para>
/// </summary>
public class CoverageMapItem
{
    /// <summary>
    /// Dấu ngăn giữa phần đã ghi nhận và các câu hỏi còn treo khi hai thứ được GHÉP LẠI thành một dòng cho
    /// người đọc — <see cref="Summary"/> (panel tiến độ) và <c>CoverageMapParser.ToText</c> (ngữ cảnh chat
    /// của BA, bản xuất hội thoại). Chiều lưu trữ không dùng tới nó: bản đồ chỉ có <see cref="Known"/>, còn
    /// câu hỏi nằm ở cột khác. Chuỗi giữ nguyên chữ "còn thiếu:" — đây là thứ NGƯỜI DÙNG đọc trên panel và
    /// model đọc trong ngữ cảnh chat, đổi nó không sửa được gì mà làm lệch mọi bản đồ text trong hội thoại cũ.
    /// </summary>
    public const string OpenQuestionMarker = "còn thiếu:";

    public string Label { get; set; } = string.Empty;

    /// <summary>Trạng thái đã chuẩn hoá: "RÕ" | "MỘT PHẦN" | "CHƯA HỎI" | "KHÔNG ÁP DỤNG".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Phần ĐÃ ghi nhận của nhóm này — điều bản đồ coi như đã biết. Trước đây là "phần đầu của tóm tắt,
    /// tính tới chữ 'còn thiếu:'"; nay là một trường riêng nên không tầng nào phải cắt chuỗi nữa.
    /// </summary>
    public string Known { get; set; } = string.Empty;

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
    /// Các câu hỏi MỞ thuộc nhóm này — KHÔNG lưu trong bản đồ, được gắn vào lúc đọc bởi
    /// <c>CoverageMapParser.AttachQuestions</c>. Rỗng ở những tầng chỉ cần trạng thái (tiến độ, các cổng
    /// bảng), nên <see cref="Summary"/> ở đó rút về đúng phần đã ghi nhận.
    /// </summary>
    public IReadOnlyList<string> Questions { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Tóm tắt gộp cho UI: phần đã ghi nhận, nối các câu hỏi còn treo nếu có. Giữ lại đúng chuỗi mà panel
    /// "Tiến độ khai thác" (server render lẫn <c>renderCoverage()</c> trong <c>requirements.js</c>) vẫn
    /// đang hiện, nên đổi chỗ lưu câu hỏi không đổi một pixel nào trên màn hình.
    /// </summary>
    public string Summary
    {
        get
        {
            var questions = string.Join("; ", Questions.Where(q => !string.IsNullOrWhiteSpace(q)));
            if (questions.Length == 0)
                return Known;

            return string.IsNullOrWhiteSpace(Known)
                ? $"{OpenQuestionMarker} {questions}"
                : $"{Known} {OpenQuestionMarker} {questions}";
        }
    }
}
