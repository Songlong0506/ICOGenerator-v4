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
/// <b>Chỉ HẠ trạng thái, nhưng câu hỏi thì THÊM và THU về.</b> Guard không bao giờ nâng trạng thái và
/// không đụng tới dòng đã có câu hỏi kế tiếp riêng (câu của distiller cụ thể hơn câu dựng sẵn ở đây). Hạ
/// nhầm — một con số vô hại trong tóm tắt, ví dụ "3 vai trò" — thì cái giá là BA hỏi thêm một câu và người
/// dùng bấm một chip xác nhận; bỏ sót thì cả tài liệu lẫn POC dựng trên một công thức chưa ai kiểm. Cùng
/// cách cân giá với các chốt chặn còn lại của bản đồ.
/// </para>
///
/// <para>
/// <b>Vì sao phải THU câu hỏi về (<see cref="CloseOwnQuestion"/>).</b> Ví dụ đã chốt thì câu xin ví dụ chết
/// theo — nhưng không guard nào khác đóng được nó, và đó là một vòng lặp kín đã gặp thật. Câu này do CODE
/// đúc ra rồi ghi vào <see cref="Domain.Project.OpenQuestions"/>; lượt distill kế được đính chính danh sách
/// cũ nên nó chép câu ấy sang lượt sau ở trạng thái <c>MỞ</c>, kể cả khi cùng lượt ấy nó vừa xuất ra
/// <c>workedExamples</c> chứa đúng ví dụ người dùng đã gật. <see cref="CoverageStaleGapGuard"/> không cứu
/// được: nó đo câu hỏi với cột <c>known</c> của dòng, mà câu trả lời ở đây nằm ở cột <b>WorkedExamples</b>
/// — bao phủ luôn dưới ngưỡng, mãi mãi. Hệ quả: dòng «Quy tắc nghiệp vụ» kẹt <c>[MỘT PHẦN]</c> vĩnh viễn
/// (<see cref="CoveragePendingGuard"/> hạ nó vì câu hỏi còn <c>MỞ</c>), cổng readiness lấy nguyên câu ấy
/// làm câu chặn, và nút "Write Requirement" khoá.
/// </para>
///
/// <para>
/// <b>Ca thật (dự án quản lý khóa học bắt buộc, 2026-09-05).</b> Người dùng chốt ví dụ *"khóa hết hạn 30/6
/// ⇒ nhắc từ 1/6, mỗi tuần một email"* ở lượt 20–21; distiller ghi đúng ví dụ đó vào <c>workedExamples</c>
/// nhưng vẫn giữ câu xin ví dụ ở <c>MỞ</c>. Guard <c>return</c> sớm (đã có ví dụ) nên không THÊM gì, còn
/// câu cũ thì không ai dọn — cổng readiness phát lại nó, và lượt BA thật (một câu hỏi khác hẳn) bị thay
/// trọn bằng câu đã chết ấy.
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
    /// Đã có ví dụ, hoặc nhóm đã có câu hỏi MỞ riêng ⇒ không đụng gì.
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
        // "Đứng ngoài" KHÔNG có nghĩa là không làm gì: câu xin ví dụ mà guard phát ở các lượt TRƯỚC vẫn
        // đang nằm trong danh sách, và nó vừa chết đúng giây này. Thu nó về trước khi trả quyền.
        if (workedExamples.Count > 0)
        {
            CloseOwnQuestion(questions);
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
    /// XOÁ câu hỏi MỞ do CHÍNH guard này phát, khi ví dụ đã có nên câu ấy không còn trả lời được gì. Chỉ
    /// xoá, KHÔNG đánh dấu <c>ĐÃ TRẢ LỜI</c> và KHÔNG nâng trạng thái dòng — cùng ranh giới với
    /// <see cref="CoverageStaleGapGuard"/>, và vì cùng lý do: bằng chứng ở đây do LLM chắt ra chứ không
    /// phải ô người dùng tự tay bấm, nên guard không được ký tên người dùng vào một câu trả lời. Nhóm mất
    /// câu hỏi vẫn đứng <c>[MỘT PHẦN]</c> và cổng readiness rơi về nhánh PHÁT LẠI — một câu đóng lại được
    /// bằng một lượt, thay cho một câu đã hết nghĩa. Quyền nâng <c>[RÕ]</c> vẫn ở lượt distill kế tiếp.
    ///
    /// <para>
    /// So bằng HẰNG SỐ (chuẩn hoá khoảng trắng + hoa/thường), không đo tương đồng: câu này do code đúc,
    /// duy nhất trong cả hệ thống, nên khớp nguyên văn là đủ và một phép đo mờ ở đây chỉ mua thêm rủi ro
    /// xoá nhầm câu xin ví dụ mà distiller tự viết cho một quy tắc CỤ THỂ — câu đó vẫn còn sống. Cùng lẽ
    /// ấy, một câu đã bị đính cụm <see cref="AskedQuestionHistory.ReopenNote"/> sẽ KHÔNG khớp và được giữ
    /// lại: đó là lệnh MỞ LẠI nhóm do chính người dùng phát, không phải câu hỏi của guard nữa.
    /// </para>
    /// </summary>
    private static void CloseOwnQuestion(IList<OpenQuestionEntry> questions)
    {
        // Duyệt NGƯỢC để xoá tại chỗ mà không nhảy cóc phần tử — cùng cách với CoverageStaleGapGuard.
        for (var i = questions.Count - 1; i >= 0; i--)
        {
            var question = questions[i];

            // Mục đã ĐÃ TRẢ LỜI ở lại danh sách: nó chở câu trả lời và đứng ngoài mọi đường hỏi sẵn rồi,
            // còn xoá đi là mời lượt distill kế dựng lại nó (xem OpenQuestionEntry.Status).
            if (question.IsOpen && IsOwnQuestion(question.Text))
                questions.RemoveAt(i);
        }
    }

    /// <summary>Câu hỏi này có đúng là câu guard đã phát không — khớp nguyên văn sau khi chuẩn hoá.</summary>
    private static bool IsOwnQuestion(string? text)
        => string.Equals(Normalize(text), NormalizedMissingExampleQuestion, StringComparison.OrdinalIgnoreCase);

    private static readonly string NormalizedMissingExampleQuestion = Normalize(MissingExampleQuestion);

    /// <summary>Gộp mọi loại khoảng trắng về một dấu cách: một câu bị xuống dòng khác đi vẫn phải khớp.</summary>
    private static string Normalize(string? text)
        => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Tóm tắt dòng có chở con số/tỷ lệ không. Đọc CHỮ SỐ chứ không đọc từ chỉ số lượng ("vài", "một
    /// nửa"): một quy tắc đáng có ví dụ tính thử thì gần như luôn viết ra con số, còn bắt theo từ thì
    /// guard hạ nhầm cả các dòng chỉ mô tả.
    /// </summary>
    private static bool CarriesNumber(string body)
        => body.Contains('%', StringComparison.Ordinal) || DigitRegex.IsMatch(body);

    private static readonly Regex DigitRegex = new(@"\d", RegexOptions.Compiled);
}
