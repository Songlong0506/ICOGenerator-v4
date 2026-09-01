namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Luật HÌNH DẠNG áp theo NHÓM của câu hỏi — cái phanh tất định cho hai luật mà
/// <c>requirement-chat.v4.md</c> đã ghi nhưng prompt không tự cưỡng chế được:
/// "đào ngoại lệ phải hỏi MỘT MÌNH" và "đừng dừng ở «có cần báo cáo không»".
///
/// <para>
/// <b>Vì sao một chip đóng được cả một nhóm.</b> Hai nhóm dưới đây là hai nhóm DUY NHẤT mà bản đồ bao phủ
/// cho phép rơi thẳng vào <c>[KHÔNG ÁP DỤNG]</c> bằng một câu phủ định của người dùng
/// (<c>requirement-coverage.v4.md</c>: *"Người dùng nói rõ luồng này không có ngoại lệ nào"*, *"nói rõ
/// không cần báo cáo nào"*). Mà <c>[KHÔNG ÁP DỤNG]</c> là trạng thái KHÔNG có đường quay lại: cổng
/// readiness bỏ qua dòng đó, BA bị cấm hỏi lại, và bước soạn tài liệu nhận một khoảng trống không cổng nào
/// báo. Nghĩa là một chip <i>"Không có trường hợp đặc biệt"</i> — bốn chữ, một cú bấm — đóng vĩnh viễn
/// đúng nhóm mà prompt gọi là "lỗ hổng lớn nhất của tài liệu yêu cầu".
/// </para>
///
/// <para>
/// <b>Ca thật (dự án JD Libary 5, lượt 22–23).</b> BA gộp ba câu vào một thẻ, câu đầu là
/// *"Khi gán JD cho nhân viên, có trường hợp nào cần xử lý đặc biệt không?"* với chip
/// <c>["Không có trường hợp đặc biệt", "Có, để tôi mô tả"]</c>. Người dùng bấm chip đầu; dòng «Luồng ngoại
/// lệ &amp; trường hợp đặc biệt» lên <c>[KHÔNG ÁP DỤNG]</c> và ở đó tới hết buổi — trong khi chính hội
/// thoại ấy đã có sẵn một đường hỏng được kể ở lượt 9 (HRBP/HoD reject thì Manager sửa rồi submit lại),
/// tức bản đồ tự chọi với chính dòng «Chức năng &amp; luồng nghiệp vụ chính» của nó. Những thứ không bao
/// giờ được hỏi: JD đã approve rồi sửa thì sao, nhân viên chuyển orgUnit hay nghỉ việc thì JD đang gán xử
/// lý thế nào, một người có được gán hai JD cùng lúc không.
/// Cùng lượt đó, câu thứ ba — *"anh/chị có cần báo cáo hay thống kê nào không?"* với chip
/// <c>["Không cần báo cáo", "Có, để tôi mô tả"]</c> — đóng nốt nhóm «Báo cáo / thống kê», dù hai điểm đau
/// người dùng vừa nhận ở lượt 7 (*"khó biết JD nào đang gán cho ai"*, *"Manager muốn xem phải hỏi HRBP"*)
/// chính là một màn hình tra cứu.
/// </para>
///
/// <para>
/// <b>Hai luật, cùng một chiều sửa: chỉ MỞ RỘNG chỗ trả lời, không bao giờ thu hẹp.</b>
/// <list type="bullet">
/// <item><b>Hỏi một mình</b> — câu đào ngoại lệ không được đi kèm câu khác trong một lượt gộp. Prompt xếp
/// nó vào danh sách "BẮT BUỘC hỏi MỘT MÌNH" vì mỗi câu trả lời mở ra một nhánh mới: nghe xong mới biết
/// hỏi tiếp gì. Gộp nó với hai câu rời (quy mô, báo cáo) là tự bịt mắt.</item>
/// <item><b>Bỏ bộ chip có/không</b> — hỏi ngoại lệ hay hỏi báo cáo bằng một cặp chip phủ định/khẳng định là
/// đặt câu hỏi mở dưới hình dạng câu đóng: vế "Có" không chở nội dung nào, còn vế "Không" thì đóng cả
/// nhóm. Bỏ chip đi thì người dùng phải KỂ — và người thật sự không có ngoại lệ nào vẫn gõ được
/// "không có", chỉ khác là bằng lời của họ chứ không phải bằng một nút bấm sẵn.</item>
/// </list>
/// Cùng cách cân giá với <see cref="BAChatReplyParser"/>: mở nhầm một câu thì người dùng mất tiện ích bấm
/// chip; bỏ sót thì một nhóm đóng vĩnh viễn mà không tầng nào biết.
/// </para>
/// </summary>
public static class InterviewQuestionRules
{
    /// <summary>
    /// Nhãn nhóm (theo <c>requirement-coverage.v4.md</c>) của các câu BẮT BUỘC đứng một mình trong lượt.
    /// Hiện chỉ có nhóm ngoại lệ: các câu đào sâu khác (ví dụ số, kịch bản luồng, gỡ mâu thuẫn, xin lời
    /// kể) không mang nhãn nhóm riêng nào đọc được từ một lượt trả lời, nên chúng vẫn thuộc phần prompt.
    /// </summary>
    private static readonly string[] AskAloneGroups = { "Luồng ngoại lệ" };

    /// <summary>
    /// Nhãn nhóm của các câu KHÔNG được hỏi bằng một cặp chip có/không — hai nhóm mà một tiếng "không"
    /// đưa thẳng dòng bản đồ tới <c>[KHÔNG ÁP DỤNG]</c>.
    /// </summary>
    private static readonly string[] NoYesNoChipGroups = { "Luồng ngoại lệ", "Báo cáo" };

    /// <summary>Câu này có thuộc nhóm bắt buộc hỏi một mình không.</summary>
    public static bool MustAskAlone(string? group) => Matches(group, AskAloneGroups);

    /// <summary>
    /// Câu này có phải "câu đóng nhóm bằng một cú bấm" không: thuộc nhóm cấm chip có/không VÀ bộ chip
    /// đúng là một cặp có/không. Bộ chip khác (liệt kê từng loại báo cáo chẳng hạn) vẫn được giữ nguyên —
    /// thứ bị cấm là hình dạng phủ-định/khẳng-định, không phải mọi chip của hai nhóm này.
    /// </summary>
    public static bool IsGroupClosingYesNo(string? group, IReadOnlyList<string> suggestions)
        => Matches(group, NoYesNoChipGroups) && IsYesNoPair(suggestions);

    /// <summary>
    /// Bộ chip chỉ gồm một vế PHỦ ĐỊNH và một vế KHẲNG ĐỊNH rỗng ruột — *"Không có trường hợp đặc biệt"* /
    /// *"Có, để tôi mô tả"*. Nhận diện theo HÌNH DẠNG (đúng hai chip, một mở đầu bằng "không", một mở đầu
    /// bằng "có") chứ không theo mặt chữ, để model đổi vài từ vẫn không lọt.
    ///
    /// <para>
    /// Cố ý KHÔNG bắt bộ hai chip xác nhận (<c>["Đúng rồi", "Không, tính khác"]</c>): ở đó vế "không" là
    /// một nhánh trả lời thật của câu hỏi chốt, và prompt kê sẵn bộ đó. Phép thử phân biệt được hai ca vì
    /// nó đòi đúng một vế mở đầu bằng "có" — bộ xác nhận không có vế nào như vậy.
    /// </para>
    /// </summary>
    public static bool IsYesNoPair(IReadOnlyList<string> suggestions)
    {
        if (suggestions.Count != 2)
            return false;

        var negatives = suggestions.Count(s => StartsWithWord(s, "không"));
        var positives = suggestions.Count(s => StartsWithWord(s, "có"));
        return negatives == 1 && positives == 1;
    }

    private static bool StartsWithWord(string? text, string word)
    {
        var value = (text ?? string.Empty).TrimStart().ToLowerInvariant();
        return value.Equals(word, StringComparison.Ordinal)
            || value.StartsWith(word + " ", StringComparison.Ordinal)
            || value.StartsWith(word + ",", StringComparison.Ordinal);
    }

    // So khớp hai chiều bằng TIỀN TỐ, cùng lý do với CoveragePendingGuard.FindGap: model viết
    // "Luồng ngoại lệ" hay "Luồng ngoại lệ & trường hợp đặc biệt" thì vẫn là một nhóm, và một phép so
    // nguyên văn sẽ làm luật này câm trong im lặng.
    private static bool Matches(string? group, IReadOnlyList<string> labels)
    {
        var value = (group ?? string.Empty).Trim();
        if (value.Length == 0)
            return false;

        return labels.Any(label =>
            value.StartsWith(label, StringComparison.OrdinalIgnoreCase)
            || label.StartsWith(value, StringComparison.OrdinalIgnoreCase));
    }
}
