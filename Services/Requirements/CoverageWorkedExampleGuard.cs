using System.Text.RegularExpressions;
using ICOGenerator.Contracts.Requirements;

namespace ICOGenerator.Services.Requirements;

/// <summary>
/// Chốt chặn TẤT ĐỊNH: một dòng «Quy tắc nghiệp vụ &amp; ràng buộc» CHỞ CON SỐ không được đứng
/// <c>[RÕ]</c> khi dự án chưa chốt được ví dụ tính thử nào (<see cref="Domain.Project.WorkedExamples"/>
/// rỗng). Chạy ở đường GHI của bản đồ bao phủ, cùng chỗ với
/// <see cref="CoverageStaleGapGuard"/> và <see cref="CoverageConfirmedTableGuard"/>.
///
/// <para>
/// <b>Vì sao con số là ca riêng.</b> <c>requirement-chat.v4.md</c> gọi công thức hiểu sai là "lỗi ĐẮT
/// nhất": tài liệu sẽ ghi đúng… điều đã hiểu sai, và mọi bước sau (kể cả POC) đều sai theo mà không cổng
/// nào bắt được — vì các cổng chỉ hỏi "có thông tin chưa", không hỏi "thông tin đó có đúng không". Cái
/// duy nhất bắt được là một ví dụ số đã được người dùng xác nhận: nó vừa là bằng chứng hiểu đúng, vừa là
/// oracle mà bản demo bị chấm theo (<c>PocWorkedExampleOracle</c>). Vì vậy prompt bắt BA "tự dựng MỘT ví
/// dụ số theo cách bạn hiểu rồi xin xác nhận" — và cho tới nay không có gì cưỡng chế điều đó.
/// </para>
///
/// <para>
/// <b>Guard này KHÔNG còn độc lập, và phải đọc nó với điều đó trong đầu.</b> Danh sách ví dụ từng do một
/// lời gọi LLM RIÊNG chắt ra ở hậu kỳ lượt chat; guard đứng giữa hai lời gọi nên nó chấm bản đồ bằng một
/// bằng chứng mà chính lượt viết bản đồ không tạo ra được. Nay cả hai ra đời trong cùng một lời gọi
/// (<see cref="CoverageDistillDocument"/>): model muốn dòng «Quy tắc nghiệp vụ» đứng <c>[RÕ]</c> chỉ cần
/// kèm một mục <c>workedExamples</c> trông giống ví dụ. Guard vì vậy tụt từ một CHỐT CHẶN xuống một luật
/// của prompt được cưỡng chế bằng code: nó vẫn bắt được ca model quên hẳn ví dụ (ca thường gặp nhất), và
/// vẫn là chỗ DUY NHẤT phát ra câu hỏi xin ví dụ, nhưng không còn bắt được model tự cấp bằng chứng cho
/// mình. Đổi lại nó đọc được danh sách của CHÍNH lượt này thay vì bản cũ một lượt. Đánh đổi có chủ đích —
/// xem <c>docs/requirement-flow.md</c>, mục "Ví dụ đã xác nhận về chung lượt distill".
/// </para>
///
/// <para>
/// <b>Ca thật (dự án JD Libary 5, lượt 13).</b> Người dùng nêu *"Responsibility (5 cái và có %, và có 1
/// item mặc định không được sửa là «Other task assign by manager» % từ 5-10)"*. BA ghi nhận nguyên văn rồi
/// đi tiếp; dòng «Quy tắc nghiệp vụ &amp; ràng buộc» chở đủ các con số ấy, mục "Ví dụ đã xác nhận" thì
/// trống trơn suốt buổi. Ba câu không ai trả lời: 5 là cố định hay tối thiểu, tổng % có phải bằng 100
/// không, và khoảng 5–10 là của riêng dòng mặc định hay của mọi dòng. Bản demo sẽ phải tự đoán cả ba.
/// </para>
///
/// <para>
/// <b>Một chiều VỀ TRẠNG THÁI, hai chiều về CÂU HỎI.</b> Guard không bao giờ nâng trạng thái và không đụng
/// tới dòng đã có câu hỏi kế tiếp riêng (câu của distiller cụ thể hơn câu dựng sẵn ở đây). Hạ nhầm —
/// một con số vô hại trong tóm tắt, ví dụ "3 vai trò" — thì cái giá là BA hỏi thêm một câu và người dùng
/// bấm một chip xác nhận; bỏ sót thì cả tài liệu lẫn POC dựng trên một công thức chưa ai kiểm. Cùng cách
/// cân giá với các chốt chặn còn lại của bản đồ.
/// </para>
///
/// <para>
/// <b>Nhưng câu hỏi thì guard PHẢI tự dọn, và đó là nửa từng thiếu.</b> Nó đặt câu xin ví dụ xuống khi
/// <see cref="Domain.Project.WorkedExamples"/> rỗng, nên nó cũng là chỗ DUY NHẤT gỡ được câu ấy khi danh
/// sách hết rỗng: câu trả lời nằm ở một CỘT KHÁC bản đồ, mà mọi guard xoá câu hỏi khác đều chỉ đọc bản đồ.
/// Thiếu nhánh gỡ thì một câu hỏi người dùng ĐÃ trả lời sống mãi trong <c>OpenQuestions</c>, giữ dòng quy
/// tắc ở <c>[MỘT PHẦN]</c> vĩnh viễn và bị cổng readiness phát lại mỗi lượt — xem
/// <see cref="ReleaseMissingExampleQuestion"/> cho ca thật và cho lý do không guard nào khác cứu được.
/// </para>
///
/// <para>
/// <b>Chỉ soi nhóm quy tắc.</b> Con số nằm ở dòng «Dữ liệu / danh mục chính» (số trường, số dòng) hay
/// «Quy mô sử dụng» (số người dùng) không phải công thức và không cần oracle. Mở rộng phạm vi ra là biến
/// guard này thành một cái cổng đóng thường trực.
/// </para>
/// </summary>
public static class CoverageWorkedExampleGuard
{
    /// <summary>Nhãn nhóm bị soi — so khớp hai chiều bằng tiền tố như các guard khác.</summary>
    private const string RuleGroupLabel = "Quy tắc nghiệp vụ";

    /// <summary>
    /// Câu hỏi dựng sẵn. Kết bằng dấu hỏi để <see cref="RequirementReadinessGate"/> phát nguyên văn (nó chỉ
    /// nối thêm đuôi "anh/chị cho mình xin thông tin này nhé?" vào câu KHÔNG có dấu hỏi), và cố ý hỏi bằng
    /// ngôn ngữ người dùng: "một ví dụ cụ thể tính ra kết quả thế nào" chứ không phải "worked example".
    /// </summary>
    public const string MissingExampleQuestion =
        "với quy tắc có con số ở trên, anh/chị cho mình một ví dụ cụ thể tính ra kết quả thế nào?";

    /// <summary>
    /// Hạ <c>[RÕ]</c> → <c>[MỘT PHẦN]</c> và THÊM một câu hỏi cho dòng quy tắc chở con số khi
    /// <paramref name="workedExamples"/> (đã đọc sẵn qua <c>InterviewOutlookParser</c>) chưa có ví dụ nào.
    /// Nhóm đã có câu hỏi MỞ riêng ⇒ không đụng gì. Đã có ví dụ ⇒ GỠ câu hỏi mà chính guard này đặt xuống
    /// ở các lượt trước (xem <see cref="ReleaseMissingExampleQuestion"/>) rồi đứng ngoài.
    /// </summary>
    public static void Apply(
        IReadOnlyList<CoverageMapItem> items,
        IList<OpenQuestionEntry> questions,
        IReadOnlyList<string> workedExamples)
    {
        // Có ví dụ nào đã chốt ⇒ quy tắc định lượng của dự án này đã qua một vòng kiểm chứng, guard đứng
        // ngoài. Đây là điều kiện MỘT ví dụ chứ không phải "mỗi quy tắc một ví dụ": bản đồ không mang cấu
        // trúc để nối ví dụ với quy tắc, và một cổng đòi nhiều hơn mức nó kiểm được là một cổng đóng mãi.
        //
        // Nhưng "đứng ngoài" KHÔNG được phép là một `return` trần: câu hỏi mà guard đặt xuống hồi danh sách
        // còn rỗng vẫn nằm trong `Project.OpenQuestions`, và không ai khác gỡ nổi nó — xem
        // ReleaseMissingExampleQuestion cho vòng lặp kín mà một `return` trần dựng ra.
        if (workedExamples.Count > 0)
        {
            ReleaseMissingExampleQuestion(questions);
            return;
        }

        foreach (var item in items)
        {
            if (!CoverageMapParser.IsSameGroup(item.Label, RuleGroupLabel))
                continue;

            if (item.Status is not ("RÕ" or "MỘT PHẦN"))
                continue;

            // Nhóm đã có câu hỏi MỞ riêng ⇒ để nguyên: câu của distiller bám vào đúng quy tắc còn hụt, cụ
            // thể hơn câu dựng sẵn ở đây, và chồng hai câu lên nhau thì cổng hỏi dồn trong một lượt.
            if (questions.Any(q => q.IsOpen && CoverageMapParser.IsSameGroup(item.Label, q.Group)))
                continue;

            if (!CarriesNumber(item.KnownText))
                continue;

            item.Status = "MỘT PHẦN";
            questions.Add(new OpenQuestionEntry { Group = item.Label, Text = MissingExampleQuestion });
        }
    }

    /// <summary>
    /// GỠ câu hỏi mà chính guard này đã đặt xuống, khi danh sách ví dụ không còn rỗng. Đối xứng với nhánh
    /// THÊM ở <see cref="Apply"/> — và đây là nửa từng thiếu.
    ///
    /// <para>
    /// <b>Vì sao một <c>return</c> trần là một vòng lặp kín.</b> Guard chỉ ĐỌC <c>WorkedExamples</c> để
    /// quyết định có thêm câu hỏi hay không; nó không sở hữu ô nào trong bản đồ. Nên khi danh sách chuyển
    /// từ rỗng sang có mục, câu hỏi cũ ở lại <c>Project.OpenQuestions</c> và <b>không guard nào khác gỡ nổi
    /// nó</b>: <see cref="CoverageStaleGapGuard"/> chỉ đối chiếu từ của câu hỏi với <c>known</c> của bản đồ,
    /// mà câu trả lời ở đây nằm ở một CỘT KHÁC (<c>WorkedExamples</c>) — cột guard ấy không đọc, nên bao phủ
    /// đo được xấp xỉ 0 và nó im lặng; <see cref="CoverageQuestionGuard"/> chỉ giết câu rỗng nghĩa, câu
    /// tường thuật và câu của hai nhóm chốt-bằng-bảng, mà câu này hình dạng hoàn toàn hợp lệ. Lượt distill
    /// cũng không tự đóng được: cửa sổ đầu vào của nó chỉ chở CÁC LƯỢT MỚI, nên lượt người dùng gật ví dụ
    /// đã trôi khỏi tầm nhìn từ lâu, và nó chép nguyên mục cũ sang lượt sau như luật ảnh-chụp-lũy-tiến đòi.
    /// </para>
    ///
    /// <para>
    /// <b>Ca thật (dự án quản lý khóa học bắt buộc, 2026-09-05).</b> Người dùng chốt ví dụ *"khóa hết hạn
    /// 30/6 ⇒ gửi mail từ 1/6, mỗi tuần một lần"* ở lượt 21 và distiller ghi đúng nó vào
    /// <c>workedExamples</c>. Ba mươi lượt sau, câu hỏi cũ vẫn đứng <c>MỞ</c>:
    /// <see cref="CoveragePendingGuard"/> giữ dòng «Quy tắc nghiệp vụ» ở <c>[MỘT PHẦN]</c> (nút
    /// "Write Requirement" khóa vĩnh viễn), ngữ cảnh chat bày nó ra ở khối "Điểm cần làm rõ còn tồn đọng"
    /// kèm lệnh ƯU TIÊN hỏi, model dựng lại đúng ví dụ 30/6 của lượt 20, phanh chống hỏi lại
    /// (<see cref="AskedQuestionHistory.IsRepeat"/>) chặn nó ở bao phủ 1.00 / Jaccard 0.80, rồi
    /// <see cref="RequirementReadinessGate"/> phát ra CHÍNH câu hỏi mồ côi này thay cho lượt của model.
    /// Người dùng nhận lại một bản chung chung hơn của câu họ đã trả lời — mỗi lượt, mãi mãi.
    /// </para>
    ///
    /// <para>
    /// <b>XOÁ chứ không đánh dấu <c>ĐÃ TRẢ LỜI</c></b> — cùng lý lẽ với <see cref="CoverageStaleGapGuard"/>:
    /// bằng chứng ở đây do LLM chắt (danh sách ví dụ là đầu ra của lượt distill), nên guard không được phép
    /// ký tên người dùng vào một câu trả lời. Khác <see cref="CoverageConfirmedTableGuard"/>, nơi bằng chứng
    /// là từng ô người dùng tự tay bấm. Xoá cũng là phép sửa ỔN ĐỊNH ở đây: khối echo của lượt sau đọc từ
    /// cột vừa ghi nên mục đã biến mất, còn nhánh THÊM thì chỉ chạy lại khi danh sách ví dụ rỗng trở lại
    /// (người dùng bác ví dụ duy nhất) — đúng lúc câu hỏi ấy đáng sống lại.
    /// </para>
    ///
    /// <para>
    /// Chỉ đụng mục <c>MỞ</c> và chỉ khớp NGUYÊN VĂN <see cref="MissingExampleQuestion"/> (chuẩn hoá qua
    /// <see cref="AskedQuestionHistory.Key"/> để một lượt echo lệch dấu câu không làm phép so câm): đây là
    /// câu của chính guard, không phải câu distiller viết, nên khớp hẹp là đủ và không có chỗ cho xoá oan.
    /// Mục đã đánh dấu <c>ĐÃ TRẢ LỜI</c> thì để nguyên — nó đứng ngoài mọi đường hỏi và còn là trí nhớ giữ
    /// cho lượt distill khỏi dựng lại nó.
    /// </para>
    /// </summary>
    private static void ReleaseMissingExampleQuestion(IList<OpenQuestionEntry> questions)
    {
        var planted = AskedQuestionHistory.Key(MissingExampleQuestion);

        // Duyệt NGƯỢC để xoá tại chỗ mà không nhảy cóc phần tử — cùng khuôn với hai guard xoá kia.
        for (var i = questions.Count - 1; i >= 0; i--)
        {
            if (questions[i].IsOpen
                && string.Equals(AskedQuestionHistory.Key(questions[i].Text), planted, StringComparison.Ordinal))
            {
                questions.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Tóm tắt dòng có chở con số/tỷ lệ không. Đọc CHỮ SỐ chứ không đọc từ chỉ số lượng ("vài", "một
    /// nửa"): một quy tắc đáng có ví dụ tính thử thì gần như luôn viết ra con số, còn bắt theo từ thì
    /// guard hạ nhầm cả các dòng chỉ mô tả.
    /// </summary>
    private static bool CarriesNumber(string body)
        => body.Contains('%', StringComparison.Ordinal) || DigitRegex.IsMatch(body);

    private static readonly Regex DigitRegex = new(@"\d", RegexOptions.Compiled);
}
