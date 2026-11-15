using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Luật HÌNH DẠNG áp theo NHÓM của câu hỏi — cái phanh tất định cho hai luật mà
/// <c>requirement-chat.v4.md</c> đã ghi nhưng prompt không tự cưỡng chế được:
/// "đào ngoại lệ phải hỏi MỘT MÌNH" và "đừng dừng ở «có cần báo cáo không»".
///
/// <para>
/// <b>Vì sao một chip đóng được cả một nhóm.</b> Hai nhóm dưới đây là hai nhóm DUY NHẤT mà bản đồ bao phủ
/// cho phép rơi thẳng vào <c>[KHÔNG ÁP DỤNG]</c> bằng một câu phủ định của người dùng
/// (<c>requirement-coverage.v5.md</c>: *"Người dùng nói rõ luồng này không có ngoại lệ nào"*, *"nói rõ
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
/// <item><b>Hỏi một mình</b> — câu đào ngoại lệ dạng MỞ không được đi kèm câu khác trong một lượt gộp.
/// Prompt xếp nó vào danh sách "BẮT BUỘC hỏi MỘT MÌNH" vì mỗi câu trả lời mở ra một nhánh mới: nghe xong
/// mới biết hỏi tiếp gì. Gộp nó với hai câu rời (quy mô, báo cáo) là tự bịt mắt. Câu ĐÓNG có chip cụ thể
/// của cùng nhóm thì KHÔNG bị ép — xem <see cref="MustAskAlone(BAChatQuestion)"/>.</item>
/// <item><b>Bỏ bộ chip có/không</b> (<see cref="OpenGroupClosingYesNo"/>, theo phán quyết của lượt trọng
/// tài) — hỏi ngoại lệ hay hỏi báo cáo bằng một cặp chip phủ định/khẳng định là
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
    /// Khoá nhóm (<see cref="CoverageGroupKey"/>) của các câu BẮT BUỘC đứng một mình trong lượt.
    /// Hiện chỉ có nhóm ngoại lệ: các câu đào sâu khác (ví dụ số, kịch bản luồng, gỡ mâu thuẫn, xin lời
    /// kể) không mang nhãn nhóm riêng nào đọc được từ một lượt trả lời, nên chúng vẫn thuộc phần prompt.
    /// </summary>
    private static readonly string[] AskAloneGroups = { CoverageGroupKey.Exceptions };

    /// <summary>
    /// Khoá nhóm của các câu KHÔNG được hỏi bằng một cặp chip có/không — hai nhóm mà một tiếng "không"
    /// đưa thẳng dòng bản đồ tới <c>[KHÔNG ÁP DỤNG]</c>.
    /// </summary>
    private static readonly string[] NoYesNoChipGroups = { CoverageGroupKey.Exceptions, CoverageGroupKey.Reports };

    /// <summary>Nhãn nhóm này có thuộc nhóm của các câu bắt buộc hỏi một mình không.</summary>
    public static bool IsAskAloneGroup(string? group) => Matches(group, AskAloneGroups);

    /// <summary>
    /// Câu này có bắt buộc đứng một mình trong lượt không: thuộc nhóm ngoại lệ VÀ là câu MỞ (không còn
    /// chip nào — do model khai <c>openEnded</c>, hoặc do cặp chip có/không của nó vừa bị xoá).
    ///
    /// <para>
    /// Xét theo HÌNH DẠNG câu hỏi chứ không chỉ theo nhãn nhóm. Thứ luật này bảo vệ là câu ĐÀO — câu mà
    /// nghe trả lời xong mới biết hỏi tiếp gì. Một câu ĐÓNG có chip cụ thể của cùng nhóm (*"Một nhân viên
    /// có được giữ nhiều tủ cùng lúc không?"* với <c>["Được, mỗi loại tủ một cái", "Mỗi người chỉ giữ một
    /// tủ", "Được giữ nhiều tủ, không giới hạn"]</c>) thì không phải câu đào: mỗi chip là một câu trả lời
    /// trọn vẹn, không chip nào đưa dòng bản đồ tới <c>[KHÔNG ÁP DỤNG]</c>, và nó qua đúng phép thử gộp
    /// của prompt (câu trả lời không làm đổi câu hỏi kế tiếp).
    /// </para>
    ///
    /// <para>
    /// <b>Ca thật (dự án Locker management, log BAChat 2026-11-15).</b> BA gộp bốn câu đóng rời nhau: cách
    /// gửi request tủ, câu "giữ nhiều tủ" ở trên, xử lý PersonalNumber không khớp, ai cập nhật danh sách
    /// tủ. Khi luật còn xét theo nhãn nhóm, câu thứ hai mang nhãn «Luồng ngoại lệ» (đúng như prompt gợi ý
    /// cho nhóm này) nên ba câu kia bị bỏ: người dùng thấy câu dẫn *"mình hỏi nhanh mấy điểm rời nhau"*
    /// trên đúng một câu hỏi, và mất ba vòng đi-về cho ba câu lẽ ra bấm xong trong một lượt.
    /// </para>
    /// </summary>
    public static bool MustAskAlone(BAChatQuestion question)
        => IsAskAloneGroup(question.Group) && (question.OpenEnded || question.Suggestions.Count == 0);

    /// <summary>
    /// MỞ các "câu đóng nhóm bằng một cú bấm": câu thuộc nhóm cấm chip có/không mà bộ chip đúng là một cặp
    /// có/không ⇒ bỏ chip, thành câu MỞ. Sửa <paramref name="questions"/> tại chỗ, KHÔNG bỏ câu nào (số
    /// thứ tự câu phải đứng yên — phanh chống hỏi lại đọc cùng một phán quyết theo số thứ tự). Trả về
    /// <c>true</c> khi có câu bị mở.
    ///
    /// <para>
    /// <b>Hai nửa, hai chủ.</b> "Bộ chip này là một cặp có/không" là câu hỏi về NGHĨA — *"Không cần báo
    /// cáo"* / *"Có, để tôi mô tả"*, *"No"* / *"Yes, let me explain"* — nên do lượt trọng tài phán
    /// (<c>TurnJudgeVerdict.YesNoChips</c>, truyền vào qua <paramref name="judgedYesNo"/>: số thứ tự câu,
    /// câu đầu là 1). Ở đây từng là một phép dò "một chip mở đầu bằng «không», một chip mở đầu bằng «có»":
    /// đúng với tiếng Việt, câm với mọi ngôn ngữ khác. Code chỉ giữ phần HÌNH DẠNG, và nó là chốt chặn
    /// của chính phán quyết: câu phải thuộc đúng hai nhóm bị cấm (theo KHOÁ nhóm) và có ĐÚNG HAI chip —
    /// trọng tài phán gì thì cũng không mở được một câu có ba chip hay một câu của nhóm khác.
    /// </para>
    ///
    /// <para>
    /// Bộ chip khác của hai nhóm này (liệt kê từng loại báo cáo chẳng hạn) được giữ nguyên — thứ bị cấm là
    /// hình dạng phủ-định/khẳng-định, không phải mọi chip. Bộ hai chip xác nhận (<c>["Đúng rồi", "Không,
    /// tính khác"]</c>) cũng không bị đụng: ở đó vế "không" là một nhánh trả lời thật của câu hỏi chốt, và
    /// prompt của trọng tài loại nó ra.
    /// </para>
    /// </summary>
    public static bool OpenGroupClosingYesNo(IReadOnlyList<BAChatQuestion> questions, IReadOnlyCollection<int> judgedYesNo)
    {
        var changed = false;
        for (var i = 0; i < questions.Count; i++)
        {
            var question = questions[i];
            if (!judgedYesNo.Contains(i + 1)
                || question.Suggestions.Count != 2
                || !Matches(question.Group, NoYesNoChipGroups))
                continue;

            question.Suggestions = new List<string>();
            question.MultiSelect = false;
            question.OpenEnded = true;
            changed = true;
        }

        return changed;
    }

    // Trường `group` của một câu hỏi là NHÃN do model chép lại, không phải khoá — CoverageGroups.KeyOf kéo
    // nó về khoá (khớp nhãn ở mọi ngôn ngữ, hai chiều bằng tiền tố: "Luồng ngoại lệ" hay "Luồng ngoại lệ &
    // trường hợp đặc biệt" vẫn là một nhóm). Một phép so nguyên văn sẽ làm luật này câm trong im lặng.
    private static bool Matches(string? group, IReadOnlyList<string> keys)
        => CoverageGroups.KeyOf(group) is { } key && keys.Contains(key);
}
