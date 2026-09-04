namespace ICOGenerator.Contracts.Requirements;

/// <summary>
/// Một dòng của "Bản đồ bao phủ yêu cầu": nhóm thông tin, trạng thái khai thác
/// ([RÕ]/[MỘT PHẦN]/[CHƯA HỎI]/[KHÔNG ÁP DỤNG]), và những điều đã ghi nhận được về nhóm ấy.
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

    /// <summary>
    /// Dấu ngăn giữa hai mẩu <see cref="Known"/> khi cả danh sách được ghép thành MỘT dòng cho model đọc
    /// (<c>CoverageMapParser.ToText</c>) và cho fixture của test đọc ngược lại. Cố ý KHÔNG phải dấu chấm
    /// hay chấm phẩy: một mẩu ghi nhận có dấu câu bên trong nó, nên chỉ một ký tự không bao giờ xuất hiện
    /// trong văn xuôi nghiệp vụ mới tách lại được đúng danh sách đã ghép.
    /// </summary>
    public const string KnownSeparator = " | ";

    public string Label { get; set; } = string.Empty;

    /// <summary>Trạng thái đã chuẩn hoá: "RÕ" | "MỘT PHẦN" | "CHƯA HỎI" | "KHÔNG ÁP DỤNG".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Những điều đã ghi nhận về nhóm này, <b>mỗi ý một phần tử</b> — điều bản đồ coi như đã biết.
    /// <para>
    /// <b>Vì sao là danh sách chứ không phải một tóm tắt.</b> Một ô chuỗi duy nhất bị lượt chắt lọc kế
    /// tiếp viết đè, nên mỗi lượt nó lại phải nén cả nhóm về "tối đa ~2 câu": chi tiết người dùng kể ở
    /// lượt 3 bị chính lượt 10 ép ra ngoài, trong khi bước soạn Product Brief lại cần đúng những chi tiết
    /// đó — và với buổi phỏng vấn dài thì các lượt cũ đã bị <c>BriefContextWindow</c> nén khỏi transcript,
    /// tức bản đồ là chỗ DUY NHẤT còn giữ chúng. Danh sách thì một ý mới chỉ thêm một phần tử, không phải
    /// mua chỗ bằng cách đẩy một ý cũ ra.
    /// </para>
    /// <para>
    /// <b>Vẫn là TRẠNG THÁI MỚI NHẤT, không phải nhật ký.</b> Người dùng nói A rồi sửa thành B thì phần tử
    /// A bị XOÁ và thay bằng B — không giữ cả hai kèm một câu "đã đính chính". Một danh sách chở cả các
    /// phiên bản đã chết thì tầng nào đọc nó (BA dẫn lượt sau, bước soạn tài liệu) cũng phải tự đoán câu
    /// nào còn hiệu lực, và đó đúng là thứ bản đồ sinh ra để khỏi phải đoán.
    /// </para>
    /// <para>
    /// <b>Mỗi phần tử phải bám lời NGƯỜI DÙNG.</b> Đây cũng là chỗ gánh vai trò của trường <c>evidence</c>
    /// cũ (một trích dẫn nguyên văn riêng cho mỗi dòng): trường ấy đã bị bỏ vì phép cắt độ dài của bản đồ
    /// hay cắt nó giữa từ, mà một trích dẫn bị cắt thì không tìm lại được trong hội thoại — tức nó mất
    /// đúng công dụng duy nhất của mình trong khi vẫn tốn chỗ ở MỌI lượt chat. Luật chống bịa chuyển sang
    /// đây: xem <c>Prompts/BusinessAnalyst/requirement-coverage.v5.md</c>.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Known { get; set; } = Array.Empty<string>();

    public bool IsCore { get; set; }

    /// <summary>
    /// Các câu hỏi MỞ thuộc nhóm này — KHÔNG lưu trong bản đồ, được gắn vào lúc đọc bởi
    /// <c>CoverageMapParser.AttachQuestions</c>. Rỗng ở những tầng chỉ cần trạng thái (tiến độ, các cổng
    /// bảng), nên <see cref="Summary"/> ở đó rút về đúng phần đã ghi nhận.
    /// </summary>
    public IReadOnlyList<string> Questions { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Phần đã ghi nhận đọc lên thành VĂN XUÔI: các mẩu <see cref="Known"/> nối bằng dấu cách, mỗi mẩu
    /// được đóng bằng một dấu kết câu nếu nó chưa có. Đây là dạng dành cho NGƯỜI đọc và cho các phép so
    /// theo từ — panel tiến độ, nhánh PHÁT LẠI của <c>RequirementReadinessGate</c> ("Mình đang ghi
    /// nhận: …"), <c>CoverageStaleGapGuard</c>. Dạng dành cho MODEL đọc thì ngăn bằng
    /// <see cref="KnownSeparator"/> để tách lại được — xem <c>CoverageMapParser.ToText</c>.
    /// </summary>
    public string KnownText => string.Join(" ", Known.Where(x => !string.IsNullOrWhiteSpace(x)).Select(EndSentence));

    /// <summary>
    /// Tóm tắt gộp cho UI: phần đã ghi nhận, nối các câu hỏi còn treo nếu có. Giữ lại đúng chuỗi mà panel
    /// "Tiến độ khai thác" (server render lẫn <c>renderCoverage()</c> trong <c>requirements.js</c>) vẫn
    /// đang hiện, nên đổi chỗ lưu câu hỏi không đổi một pixel nào trên màn hình.
    /// </summary>
    public string Summary
    {
        get
        {
            var known = KnownText;
            var questions = string.Join("; ", Questions.Where(q => !string.IsNullOrWhiteSpace(q)));
            if (questions.Length == 0)
                return known;

            return known.Length == 0
                ? $"{OpenQuestionMarker} {questions}"
                : $"{known} {OpenQuestionMarker} {questions}";
        }
    }

    // Một mẩu ghi nhận được viết như một câu, nhưng model hay bỏ dấu kết ở mẩu cuối. Nối trần thì hai mẩu
    // dính vào nhau thành một câu vô nghĩa ngay trong lời phát lại mà người dùng phải rà.
    private static string EndSentence(string text)
    {
        var body = text.Trim();
        return body.Length == 0 || body.EndsWith('.') || body.EndsWith(';') || body.EndsWith('?') || body.EndsWith('!')
            ? body
            : body + ".";
    }
}
